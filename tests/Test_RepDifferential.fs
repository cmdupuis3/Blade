/// B3 of docs/plan-equivariance-in-types.md: the differential gate between
/// the typed representation-status deduction (Blade.DeduceRep, producer of
/// TypedCertProposals) and the elaboration-seam inference (stage 6a,
/// producer of CertSuggestions/BL4011). Ship criterion for phase B: typed
/// recall ⊇ seam recall over the corpus, with ZERO false proposals (every
/// typed-only proposal must be accepted by the seam checker when tried as
/// a pinned hypothesis).
///
/// Skeleton stub — filled by the B3 work item; wired into RunAll/Cli by
/// the same item.
module Blade.Tests.RepDifferential

open Blade.Tests.TestHarness

let runRepDifferentialTests () : BlockResult =
    { Block = "Rep Deduction Differential"; Passed = 0; Failed = 0; Skipped = 0; FailedNames = [] }
