// Test sources live on disk in tests/corpus (audit §2.3 / Phase 0.1: the
// corpus doubles as the differential oracle for the rewrite). This module
// only names the categories; edit the .blade files to change tests.
module Blade.Tests.Loops

open Blade.Tests.Corpus

/// Loop objects and application
let loopTests = category "loops"

// ============================================================================
// Multi-index-type range<I, J, ...> -- IMPLEMENTED
// ----------------------------------------------------------------------------
// This block previously described multi-index range as future work ("R1"); it
// landed and the note went stale. One range<> spans a PRODUCT of independent
// index types, uncurried into nested loop levels, one kernel param per slot:
//
//     method_for(range<Lat, Lon>) <@> lambda(lat, lon) -> A(lat, lon)
//
// Layers: Parser.fs (sepBy over the type list), TypeCheck.fs ExprRange
// (mkVirtualArrayArrow over all slots) and expandedRows (per-slot params, each
// tagged via elemTypeForIterationIndex), IRRange (index-type LIST), CodeGen's
// VirtualRange arm (binds each level's loop index).
//
// Coverage: corpus loops/067 (basic), loops/068 (mixed with a real array),
// index-types/120 (per-slot named tags) + 121 (swapped tags reject, which is
// what proves the tags are distinct rather than uniformly the last slot's).
//
// Distinct generalization, do not conflate: a SINGLE multi-rank index type
// (SymIdx<2,N>) also yields two params, by the rank rule rather than the
// product rule.
//
// Live restrictions:
//   - A CompoundIdx slot IS the whole iteration space, so it cannot share a
//     range<> with other index types (TypeCheck.fs ExprRange, formalism 4.5;
//     corpus index-types/017). A sole compound slot is fine.
//   - reverse<I> and blocked<I, K> remain SINGLE-index; blocked has no parser
//     arm at all today (AST/typecheck/codegen only).
//
// Still open -- interaction with CompoundIdx: if the sole-compound restriction
// is ever lifted, a CompoundIdx as ONE element of a multi-index range is where
// partial-indexing result types bite: the rank-2 (-> Idx) vs rank-3+ (-> smaller
// CompoundIdx) degeneration governs what a partially-resolved compound axis
// contributes to the product space. That corner needs its own coverage if and
// when it becomes reachable.
// ============================================================================
