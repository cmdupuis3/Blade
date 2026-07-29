/// Phase B of docs/plan-equivariance-in-types.md: the typecheck-resident
/// representation-status deduction — the fourth lattice made typed. This
/// module is the typed sibling of Deduce.fs (parity/sign) and the eventual
/// successor of MLEquiv's elaboration-seam inference (stage 6a), which
/// remains the CHECKING and EMITTING authority through phase B: proposals
/// produced here ride the TypedCertProposals channel and are consumed ONLY
/// by the differential harness (tests/Test_RepDifferential.fs) until the
/// B3 parity gate holds (typed recall ⊇ seam recall, zero false proposals).
///
/// Compile order: after TypedAst/Deduce, before StaticEval — TypeCheck
/// (much later) can call in; nothing here may reference TypeEnv upward.
module Blade.DeduceRep

open Blade.Ast

/// A typed-deduction certificate proposal — the structured twin of the
/// seam's suggestion strings, carrying what the differential needs to
/// compare: who, which group, the rendered signature (seam vocabulary,
/// so string comparison is meaningful), and the dependency closure.
type RepProposal = {
    Owner: string
    /// "O3" | "SO3" | "Point <g>" — the seam's groupStr vocabulary.
    Group: string
    /// Rendered like the seam's sigSummary, for differential comparison.
    Signature: string
    /// Dependency closure (unpinned helpers this proposal rests on), in
    /// decl order.
    Deps: string list
}

/// The internal channel between the typed walker (producer, TypeCheck
/// time) and the differential harness (consumer). NOT surfaced to users
/// in phase B — BL4011 stays the seam's to emit until the parity gate.
/// AsyncLocal, reset/add/get, the CertSuggestions lifecycle one phase
/// later.
module TypedCertProposals =
    let private slot = new System.Threading.AsyncLocal<(RepProposal * Span) list>()
    let reset () = slot.Value <- []
    let add (p: RepProposal) (span: Span) = slot.Value <- (p, span) :: slot.Value
    let get () : (RepProposal * Span) list =
        match box slot.Value with null -> [] | _ -> List.rev slot.Value
