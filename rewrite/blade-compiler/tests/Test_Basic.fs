// Test sources live on disk in tests/corpus (audit §2.3 / Phase 0.1: the
// corpus doubles as the differential oracle for the rewrite). This module
// only names the categories; edit the .blade files to change tests.
module Blade.Tests.Basic

open Blade.Tests.Corpus

/// Basic language constructs
let basicTests = category "basic"

/// Scalar math intrinsics (exp/log/sqrt/trig family; ML-module prerequisite)
let intrinsicsTests = category "intrinsics"

/// grad() — reverse-mode AD source transform (Grad.fs)
let adTests = category "ad"

/// End-to-end equivariant ML: forward + grad + SGD training, pinned to the
/// ml/ F# oracle (TrainingOracle.fs)
let mlE2eTests = category "ml-e2e"

/// Elaborated ML ops (MLElaborate.fs): op-level value pins + reject probes
let mlOpsTests = category "ml-ops"
