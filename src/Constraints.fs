// Open where-clause constraint registry: the extension point that lets domain
// layers (ML, PPL) own constraint keywords the core grammar doesn't know.
// The parser records unknown `where <name>(<idents>)` conjuncts as data
// (WhereClause.Custom); the checker dispatches each conjunct through this
// registry. Sibling of StaticEval's external-builtin registry. Registration
// happens during the owning module's elaboration stage, always before
// checking, so handlers are in place by the time signatures are checked.
//
// A handler is a two-phase interface -- a function carrying a registered
// conjunct is effectively promoted to the owning module's function kind:
//   Validate            signature-site well-formedness (args vs params)
//   EnterBody/ExitBody  license scope around the declaring function's body
//                       (stack discipline: the handler owns whatever state
//                       the license affects)
//   Discharge           call-site verification -- the caller must prove the
//                       declared relation for the actual arguments, via the
//                       provenance oracle the checker supplies
module Blade.Constraints

type ConstraintHandler = {
    /// One-line description, shown in "unknown constraint" diagnostics.
    Describe: string
    /// funcName -> paramNames -> conjunct args -> well-formed?
    Validate: string -> string list -> string list -> Result<unit, string>
    /// Open a license scope for the declaring function's body.
    /// funcName -> conjunct args.
    EnterBody: string -> string list -> unit
    /// Close the license scope (always called, error paths included).
    ExitBody: string -> string list -> unit
    /// Call-site discharge. funcName -> conjunct args -> provenance oracle
    /// (callee param name |-> the caller-side provenance set of the actual
    /// argument bound to it; empty = unknown).
    Discharge: string -> string list -> (string -> Set<string>) -> Result<unit, string>
}

let private handlers =
    System.Collections.Concurrent.ConcurrentDictionary<string, ConstraintHandler>()

/// Register (or re-register -- idempotent) a constraint keyword.
let registerConstraint (name: string) (handler: ConstraintHandler) : unit =
    handlers.[name] <- handler

let lookupConstraint (name: string) : ConstraintHandler option =
    match handlers.TryGetValue name with
    | true, h -> Some h
    | _ -> None

/// Registered vocabulary, for "unknown constraint" diagnostics.
let registeredConstraintNames () : string list =
    handlers.Keys |> Seq.sort |> List.ofSeq

/// The provenance token a function parameter carries inside its declaring
/// function's body. Shared here so the checker (which seeds parameter
/// provenance) and handlers (which insert license facts over these tokens
/// in EnterBody) agree on the format without coupling to each other.
let paramProvenanceToken (funcName: string) (paramName: string) : string =
    sprintf "%s.%s" funcName paramName
