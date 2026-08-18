// IDE side-channels for the type checker (AsyncLocal, reset at the top of
// every typeCheck call): BL4010 pin suggestions, the WarningLog/DeducedFacts
// re-exports of TypeEnv's channels, and per-function deduction results for
// `ide check --json`. Plus the small tuple-arity helpers that historically
// sat beside them at the top of TypeCheck.fs.
module Blade.TypeCheckIde

open Blade.Ast
open Blade.IR
open Blade.IRLoopStructure
open Blade.IRStorage
open Blade.IRLift
open Blade.IRMono
open Blade.IRPrint
open Blade.IRValidate
open Blade.Types
open Blade.TypedAst
open Blade.Unify
open Blade.TypeEnv
open Blade.Zonk

/// IDE side-channel for stage-3/4 confirm-and-pin suggestions (BL4010): the
/// structured twin of the plain-string warning the CLI prints. Each entry is
/// (message, kernel span) so editor tooling can render a ghost annotation at
/// the kernel and offer the one-click pin. Reset at the top of typeCheck,
/// recorded at buildApplyInfo's suggestion site. AsyncLocal, like IdePartial
/// (defined near typeCheck below); lives at the file head because its writer
/// (buildApplyInfo) precedes typeCheck in the compilation order.
module PinSuggestions =
    let internal slot = new System.Threading.AsyncLocal<(string * Span) list>()
    let reset () = slot.Value <- []
    let add (msg: string) (span: Span) = slot.Value <- (msg, span) :: slot.Value
    let get () : (string * Span) list =
        match box slot.Value with null -> [] | _ -> List.rev slot.Value

/// The TOP-LEVEL WIDTH of a parameter's WRITTEN type annotation, in nodes of
/// the pack's spine (docs/plan-tuples-vs-arg-packs.md 6c). `Tuple<k>` is the
/// width-only spelling; a fully written `(T1, ..., Tk)` says the same thing
/// with the element types filled in, so both count k -- that is what turns
/// 3.6's M6 (a tuple-annotated kernel param) from a miscompile into the main
/// path. Everything else is width 1. Nesting INSIDE the annotation is not
/// counted: `((A,B),(C,D))` is width 2, not 4 (diagnostics/062).
///
/// Deliberately over the SURFACE `TypeExpr`, not over the lowered `IRType`:
/// both spellings lower to `IRTTuple`, but so does an UNANNOTATED param the
/// moment a tuple-typed row unifies into it. Reading widths off the lowered
/// type would therefore make pack widths inference-dependent, which is exactly
/// the ordering cliff 5.1 rules out (kernel bodies are inferred before their
/// params are bound). Ruling 1: tuple-ness is always written.
let declaredTupleWidth (t: TypeExpr option) : int option =
    match t with
    | Some (TyTupleWidth n) when n >= 2 -> Some n
    | Some (TyTuple ts) when ts.Length >= 2 -> Some ts.Length
    | _ -> None

/// `let (a, b, c) = <2-tuple>` -- the pattern's leaf count matches NEITHER the
/// scrutinee's top-level width NOR its flattened leaf count, so there is no
/// reading under which every name gets a component.
///
/// Every destructuring site used to fall back to the structural list and let
/// FRESH INFERENCE VARIABLES cover the overflow ("Fall back to structural, let
/// fresh vars handle overflow"). Measured consequence: `blade check` says OK
/// and `g++` then rejects `std::get<2>` applied to a `std::tuple<double,
/// double>`. Shared by all four destructuring sites (`DeclLet`, `let static`,
/// block `StmtLet`, expression position) so they cannot drift.
///
/// Deliberately silent for every NON-tuple scrutinee: a `Poly` pack, a struct,
/// or a type still unresolved at this point is somebody else's judgement, and
/// answering here would turn inference order into a diagnostic.
let tupleDestructureArityError (env: TypeEnv) (pats: Pattern list) (valueTy: IRType) : TypeError option =
    match env.Subst.Resolve valueTy with
    | IRTTuple ts ->
        let flat = IR.flattenTupleLeaves (IRTTuple ts)
        if pats.Length = ts.Length || pats.Length = flat.Length then None
        else
            let flatNote =
                if flat.Length <> ts.Length
                then sprintf " (%d leaves when flattened)" flat.Length
                else ""
            Some (Other (sprintf
                    "this `let` binds %d names, but the value is a %d-tuple%s. A tuple pattern needs one name per component -- or one per flattened leaf -- so write %d, or project the components you want with `t[i]`."
                    pats.Length ts.Length flatNote ts.Length))
    | _ -> None

/// Fourth family member -- see `TypeEnv.WarningLog` for the storage and why it
/// has to live down there (its only writer is `emitWarning`, and TypeEnv cannot
/// reference upward). Re-exported here so that
/// `Blade.TypeCheck.{PinSuggestions, IdePartial, WarningLog}` is the ONE
/// namespace a drain site reads.
module WarningLog =
    let reset () = Blade.TypeEnv.WarningLog.reset ()
    let get () : Blade.Diagnostics.Diagnostic list = Blade.TypeEnv.WarningLog.get ()

/// Fifth family member -- storage in `TypeEnv.DeducedFacts` (Zonk writes to it
/// too, and Zonk cannot reference TypeCheck). Re-exported for the same
/// one-namespace reason as WarningLog.
module DeducedFacts =
    let reset () = Blade.TypeEnv.DeducedFacts.reset ()
    let get () : (Blade.TypeEnv.DeducedFact * Span) list = Blade.TypeEnv.DeducedFacts.get ()
/// IDE side-channel for the deduction RESULTS themselves -- the structured twin
/// of PinSuggestions' prose. The per-function tables (FuncDeducedPairs,
/// PackDeducedComm) and the rank pins live in the TypeEnv/Subst local to
/// checkProgram and are gone when it returns, so `ide check --json` records
/// them here as they are computed: named functions by name, lambda kernels by
/// source span. Reset alongside PinSuggestions; AsyncLocal like IdePartial.
module IdeDeductions =
    /// One lambda-kernel instantiation at the apply seam: kernel span, param
    /// names, adjacent-pair parities (n-1 entries), declared where-clause
    /// conjuncts (comm/anticomm, rendered), and per-param cell ranks.
    type KernelInfo = {
        KSpan: Span
        KParams: string list
        KParities: Blade.Deduce.Parity list
        KDeclared: string list
        KRanks: (string * int) list
    }
    let internal pairs = new System.Threading.AsyncLocal<(string * (string list * Blade.Deduce.Parity list)) list>()
    let internal packs = new System.Threading.AsyncLocal<(string * (string * Blade.Deduce.Parity)) list>()
    let internal kernels = new System.Threading.AsyncLocal<KernelInfo list>()
    let reset () =
        pairs.Value <- []
        packs.Value <- []
        kernels.Value <- []
    let addPairs (funcName: string) (paramNames: string list) (ps: Blade.Deduce.Parity list) =
        pairs.Value <- (funcName, (paramNames, ps)) :: pairs.Value
    let addPack (funcName: string) (packParam: string) (p: Blade.Deduce.Parity) =
        packs.Value <- (funcName, (packParam, p)) :: packs.Value
    let addKernel (info: KernelInfo) =
        kernels.Value <- info :: kernels.Value
    let internal read (slot: System.Threading.AsyncLocal<'a list>) =
        match box slot.Value with null -> [] | _ -> List.rev slot.Value
    let getPairs () = read pairs
    let getPacks () = read packs
    let getKernels () = read kernels

