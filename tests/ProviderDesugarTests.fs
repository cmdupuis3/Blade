// Unit pins for src/ProviderDesugar.fs -- the icechunk `checkout` desugar.
//
// Fully hermetic and in-process: the pass is a raw-AST -> raw-AST rewrite, so
// every case here is parse -> expand -> assert on the resulting nodes. No
// store, no provider registry, no g++, and deliberately no typecheck --
// `import icechunk` does not resolve until the provider itself lands
// (docs/plans/plan-icechunk-provider.md, P2), and the desugar is specified to
// run BEFORE any of that.
//
// What is pinned: both accepted call shapes, the canonical key they build,
// SPAN FIDELITY on every synthesized node (the plan's section 13 makes this
// part of the gate -- diagnostics must point at the checkout text, not at the
// key), the four untouched cases, idempotence, and the loud refusal.
module Blade.Tests.ProviderDesugarTests

open Blade
open Blade.Ast
open Blade.Tests.TestHarness

let runProviderDesugarTests () : int =
    printHeader "Provider Desugar (icechunk checkout)"
    let mutable passed = 0
    let mutable failed = 0

    let check (name: string) (condition: bool) (detail: string) =
        if condition then
            printfn "  PASS: %s" name
            passed <- passed + 1
        else
            printfn "  FAIL: %s -- %s" name detail
            failed <- failed + 1

    // ---------------------------------------------------------------
    // Fixture helpers
    // ---------------------------------------------------------------

    /// A parse failure in a fixture is a TEST bug, not a finding -- fail
    /// loudly rather than letting the case degrade into a vacuous pass.
    let parse (src: string) : Program =
        match Parser.parseProgram src with
        | Ok p -> p
        | Error e -> failwith $"test fixture does not parse ({e.Line}:{e.Col}): {e.Message}"

    /// The right-hand side of the top-level `let` / `let static` binding `name`.
    let declValue (p: Program) (name: string) : Expr option =
        p.Modules
        |> List.collect (_.Decls)
        |> List.tryPick (fun d ->
            match d.Value with
            | DeclLet b | DeclStatic b ->
                (match b.Pattern.Kind with
                 | PatternKind.PatVar n when n = name -> Some b.Value
                 | _ -> None)
            | _ -> None)

    let valueOf (p: Program) (name: string) : Expr =
        match declValue p name with
        | Some e -> e
        | None -> failwith $"test fixture has no top-level binding named {name}"

    /// (alias, path) when this expression is exactly `alias.load("path")`.
    let asLoad (e: Expr) : (string * string) option =
        match e.Kind with
        | ExprKind.ExprApp ({ Kind = ExprKind.ExprField ({ Kind = ExprKind.ExprVar a }, "load") },
                            [ { Kind = ExprKind.ExprLit (LitString path) } ]) -> Some (a, path)
        | _ -> None

    let loadOf (p: Program) (name: string) : (string * string) option =
        declValue p name |> Option.bind asLoad

    let isCheckout (e: Expr) : bool =
        match e.Kind with
        | ExprKind.ExprApp ({ Kind = ExprKind.ExprField (_, "checkout") }, _) -> true
        | _ -> false

    /// The four spans a `recv.member(arg0, ...)` call owns: the whole
    /// application, the `recv.member` field node, the receiver, and the first
    /// argument. The desugar copies all four onto its replacement, so ONE
    /// extractor serves both the before and the after shape.
    let callSpans (e: Expr) : (Span * Span * Span * Span) option =
        match e.Kind with
        | ExprKind.ExprApp (({ Kind = ExprKind.ExprField (recvE, _) } as headE), (arg0 :: _)) ->
            Some (e.Span, headE.Span, recvE.Span, arg0.Span)
        | _ -> None

    let spanStr (s: Span) = $"{s.StartLine}:{s.StartCol}-{s.EndLine}:{s.EndCol}"
    let shown (v: (string * string) option) = $"%A{v}"
    let msgs (ds: Blade.Diagnostics.Diagnostic list) =
        ds |> List.map (_.Message) |> String.concat "; "

    let repoSrc = """
import icechunk as ic

let repo = ic.load("data/weather.icechunk")
let ck1 = repo.checkout("main")
let ck2 = repo.checkout("v1.0", ic.tag)
let ck3 = repo.checkout("main", ic.branch)
let ck4 = repo.checkout("1CECHNKREP0F1RSTCMT0", ic.snapshot)
"""

    // ---------------------------------------------------------------
    // 1. The canonical key
    // ---------------------------------------------------------------
    printfn "\n--- canonical key ---"
    let markerKey = ProviderDesugar.canonicalKey "data/weather.icechunk" "tag" "v1.0"
    check "canonicalKey: marker form" (markerKey = "data/weather.icechunk@tag:v1.0") markerKey
    let bareKey = ProviderDesugar.canonicalKey "s" "?" "main"
    check "canonicalKey: bare form uses `?`" (bareKey = "s@?:main") bareKey

    // ---------------------------------------------------------------
    // 2. Bare and marker forms rewrite to the load shape
    // ---------------------------------------------------------------
    printfn "\n--- bare + marker rewrites ---"
    (match ProviderDesugar.expand (parse repoSrc) with
     | Error ds -> check "repo fixture desugars without diagnostics" false (msgs ds)
     | Ok p ->
         check "repo fixture desugars without diagnostics" true ""
         let handle = loadOf p "repo"
         check "the repo handle itself is left alone"
             (handle = Some ("ic", "data/weather.icechunk")) (shown handle)
         let l1 = loadOf p "ck1"
         check "bare checkout -> @?:main"
             (l1 = Some ("ic", "data/weather.icechunk@?:main")) (shown l1)
         let l2 = loadOf p "ck2"
         check "ic.tag -> @tag:v1.0"
             (l2 = Some ("ic", "data/weather.icechunk@tag:v1.0")) (shown l2)
         let l3 = loadOf p "ck3"
         check "ic.branch -> @branch:main"
             (l3 = Some ("ic", "data/weather.icechunk@branch:main")) (shown l3)
         let l4 = loadOf p "ck4"
         check "ic.snapshot -> @snapshot:<id>"
             (l4 = Some ("ic", "data/weather.icechunk@snapshot:1CECHNKREP0F1RSTCMT0")) (shown l4)
         check "no `checkout` node survives the rewrite"
             ([ "ck1"; "ck2"; "ck3"; "ck4" ]
              |> List.forall (fun n -> not (isCheckout (valueOf p n)))) "")

    // ---------------------------------------------------------------
    // 3. Span fidelity (plan section 13: diagnostics point at the checkout
    //    text, never at the synthesized key)
    // ---------------------------------------------------------------
    printfn "\n--- span fidelity ---"
    (let raw = parse repoSrc
     match callSpans (valueOf raw "ck2"), ProviderDesugar.expand raw with
     | Some (rawApp, rawHead, rawRecv, rawArg), Ok p ->
         // A test comparing two noSpans would pass vacuously.
         check "fixture spans are real (not noSpan)"
             (rawApp.StartLine > 0 && rawHead.StartLine > 0
              && rawRecv.StartLine > 0 && rawArg.StartLine > 0)
             (spanStr rawApp)
         (match callSpans (valueOf p "ck2") with
          | Some (newApp, newHead, newRecv, newArg) ->
              check "span: the load application keeps the checkout call's span"
                  (newApp = rawApp) $"{spanStr newApp} vs {spanStr rawApp}"
              check "span: `ic.load` keeps `repo.checkout`'s span"
                  (newHead = rawHead) $"{spanStr newHead} vs {spanStr rawHead}"
              check "span: the alias keeps the repo receiver's span"
                  (newRecv = rawRecv) $"{spanStr newRecv} vs {spanStr rawRecv}"
              check "span: the canonical key keeps the ref-name literal's span"
                  (newArg = rawArg) $"{spanStr newArg} vs {spanStr rawArg}"
          | None -> check "the rewritten ck2 is a call with spans" false "shape mismatch")
     | _, Error ds -> check "span fixture desugars" false (msgs ds)
     | None, _ -> check "the raw ck2 is a call with spans" false "shape mismatch")

    // ---------------------------------------------------------------
    // 4. `let static` bindings rewrite too -- StaticEval's providerRoots scan
    //    recognizes both, so the desugar has to serve both
    // ---------------------------------------------------------------
    printfn "\n--- static bindings ---"
    (let src = """
import icechunk as ic

let repo = ic.load("r")
let static ck = repo.checkout("main", ic.branch)
"""
     match ProviderDesugar.expand (parse src) with
     | Ok p ->
         let l = loadOf p "ck"
         check "let static checkout rewrites" (l = Some ("ic", "r@branch:main")) (shown l)
     | Error ds -> check "let static checkout rewrites" false (msgs ds))

    // ---------------------------------------------------------------
    // 5. A bare `import icechunk` (no alias) IS the alias
    // ---------------------------------------------------------------
    printfn "\n--- unaliased import ---"
    (let src = """
import icechunk

let repo = icechunk.load("r")
let ck = repo.checkout("v1", icechunk.tag)
"""
     match ProviderDesugar.expand (parse src) with
     | Ok p ->
         let l = loadOf p "ck"
         check "bare `import icechunk` binds the alias `icechunk`"
             (l = Some ("icechunk", "r@tag:v1")) (shown l)
     | Error ds -> check "bare `import icechunk` binds the alias `icechunk`" false (msgs ds))

    // ---------------------------------------------------------------
    // 6. The untouched cases
    // ---------------------------------------------------------------
    printfn "\n--- untouched receivers ---"

    // 6a. A receiver that is not a recorded repo handle: left alone, and NOT
    //     an error. It fails downstream as the ordinary missing-member error
    //     it is, which is the right message.
    (let src = """
import icechunk as ic

let repo = ic.load("r")
let bogus = 42
let ck = bogus.checkout("main")
"""
     let raw = parse src
     match ProviderDesugar.expand raw with
     | Ok p ->
         check "non-repo receiver: node untouched" (isCheckout (valueOf p "ck")) "rewritten anyway"
         check "non-repo receiver: program comes back reference-equal"
             (LanguagePrimitives.PhysicalEquality p raw) "a new Program was built"
     | Error ds -> check "non-repo receiver: no diagnostic" false (msgs ds))

    // 6b. No icechunk import at all -- the fast path. A `.checkout` on some
    //     other provider's load is none of this pass's business.
    (let src = """
import zarr as z

let store = z.load("s")
let ck = store.checkout("main")
"""
     let raw = parse src
     match ProviderDesugar.expand raw with
     | Ok p ->
         check "no icechunk import: fast path returns the SAME program"
             (LanguagePrimitives.PhysicalEquality p raw) "a new Program was built"
         check "no icechunk import: checkout node untouched"
             (isCheckout (valueOf p "ck")) "rewritten anyway"
     | Error ds -> check "no icechunk import: fast path" false (msgs ds))

    // 6c. A load whose path ALREADY carries a ref is a checkout, not a repo
    //     handle -- never recorded, which is exactly what makes the pass
    //     idempotent.
    (let src = """
import icechunk as ic

let ck0 = ic.load("r@branch:main")
let ck1 = ck0.checkout("other")
"""
     let raw = parse src
     match ProviderDesugar.expand raw with
     | Ok p ->
         check "'@' in the load path: not a repo handle, nothing recorded"
             (LanguagePrimitives.PhysicalEquality p raw) "a new Program was built"
         let l = loadOf p "ck0"
         check "'@' in the load path: the load itself is untouched"
             (l = Some ("ic", "r@branch:main")) (shown l)
     | Error ds -> check "'@' in the load path: left alone" false (msgs ds))

    // 6d. Rebinding the name drops the repo record.
    (let src = """
import icechunk as ic

let repo = ic.load("r")
let repo = 42
let ck = repo.checkout("main")
"""
     let raw = parse src
     match ProviderDesugar.expand raw with
     | Ok p ->
         check "a rebound name is no longer a repo handle"
             (LanguagePrimitives.PhysicalEquality p raw) "rewrote against a shadowed binding"
     | Error ds -> check "a rebound name is no longer a repo handle" false (msgs ds))

    // ---------------------------------------------------------------
    // 7. Idempotence -- the pass runs at more than one funnel
    // ---------------------------------------------------------------
    printfn "\n--- idempotence ---"
    (match ProviderDesugar.expand (parse repoSrc) with
     | Ok p1 ->
         (match ProviderDesugar.expand p1 with
          | Ok p2 ->
              check "a second pass is a no-op (reference-equal)"
                  (LanguagePrimitives.PhysicalEquality p2 p1)
                  "rewrote an already-desugared program"
          | Error ds -> check "a second pass is a no-op" false (msgs ds))
     | Error ds -> check "the first pass succeeds" false (msgs ds))

    // ---------------------------------------------------------------
    // 8. A wrong-shaped checkout on a RECOGNIZED repo: loud, coded, located
    // ---------------------------------------------------------------
    printfn "\n--- wrong-shaped checkout refuses ---"
    let badHeader = """
import icechunk as ic
import zarr as z

let repo = ic.load("r")
let name = "main"
let ck = """

    let refusal (label: string) (call: string) =
        let raw = parse (badHeader + call + "\n")
        let rawSpan = (valueOf raw "ck").Span
        match ProviderDesugar.expand raw with
        | Ok _ -> check $"{label}: refused" false "the pass accepted it"
        | Error ds ->
            check $"{label}: refused" (ds.Length = 1) $"{ds.Length} diagnostics"
            match ds with
            | [ d ] ->
                check $"{label}: coded BL3007" (d.Code = "BL3007") d.Code
                check $"{label}: located at the checkout call"
                    (d.Span = rawSpan && rawSpan.StartLine > 0)
                    $"{spanStr d.Span} vs {spanStr rawSpan}"
                check $"{label}: message steers to the accepted forms"
                    (d.Message.Contains "ic.tag" && d.Message.Contains "checkout")
                    d.Message
            | _ -> ()

    refusal "non-literal ref name" "repo.checkout(name)"
    refusal "unknown marker field" "repo.checkout(\"v1.0\", ic.bogus)"
    refusal "marker off another alias" "repo.checkout(\"v1.0\", z.tag)"
    refusal "string in the marker slot" "repo.checkout(\"v1.0\", \"tag\")"
    refusal "three arguments" "repo.checkout(\"v1.0\", ic.tag, ic.branch)"

    // ---------------------------------------------------------------
    // Summary
    // ---------------------------------------------------------------
    printFooter "Provider Desugar" [$"{passed} passed"; $"{failed} failed"]
    if failed > 0 then 1 else 0
