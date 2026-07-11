// Test sources live on disk in tests/corpus (audit §2.3 / Phase 0.1: the
// corpus doubles as the differential oracle for the rewrite). This module
// only names the categories; edit the .blade files to change tests.
module Blade.Tests.Loops

open Blade.Tests.Corpus

/// Loop objects and application
let loopTests = category "loops"

// ============================================================================
// R1 (FUTURE, not yet implemented): multi-index-type range<I, J, ...>
// ----------------------------------------------------------------------------
// Breadcrumb for future compiler work. The desirable ergonomic is collapsing a
// classic nested for-loop into a single co-iterated range over a PRODUCT of
// several independent index types:
//
//     for (A, B) in range<LatIdx, LonIdx> <@> lambda(lat, lon) -> A(lat, lon)
//
// Current state (verified): range<> parses and typechecks EXACTLY ONE index
// type. TExprRange carries a single idx and produces mkVirtualArrayArrow [idx];
// a comma-separated range<I, J> does not parse today. The single index type MAY
// be multi-position (SymIdx<2,N>, CompoundIdx<mask>), which is a DIFFERENT
// generalization from the multi-index-type product wanted here.
//
// Work required for R1 (three layers):
//   (1) Parser: accept a comma-separated list inside range< ... >.
//   (2) TypeCheck: build a multi-slot virtual array (mkVirtualArrayArrow over
//       the list), each listed index type contributing one product axis.
//   (3) CodeGen: rectangular co-iteration over the product (nested loops), with
//       the kernel receiving one bound variable per axis.
//
// Interaction with CompoundIdx: a CompoundIdx appearing as ONE element of a
// multi-index range is where partial-indexing result types bite -- the rank-2
// (-> Idx) vs rank-3+ (-> smaller CompoundIdx) degeneration governs what a
// partially-resolved compound axis contributes to the product space. That
// corner should get its own coverage once R1 and compound-range iteration both
// exist.
//
// Skeleton (commented out; does not compile as Blade until R1 lands):
//
// let r1_multiIndexRange = """
// let A: Array<Float like LatIdx, LonIdx> = fill_random(range<LatIdx, LonIdx>, gen)
// let out = for (a) in range<LatIdx, LonIdx> <@> lambda(lat, lon) -> A(lat, lon)
// """
// EXPECT (once implemented): rectangular co-iteration over LatIdx x LonIdx,
// element-wise, no product blow-up beyond the two declared axes.
// ============================================================================
