// THE KNOB REGISTRY: every environment variable the compiler, the programs it
// compiles and the test harness read, in ONE typed table (deep review 2, K16).
//
// A knob's entry says what it is (its kind), who reads it, its default, the
// values it accepts and what each means, whether `blade test` clears it or
// pins it (and to what), whether blade.toolchain.json mirrors it, and one line
// of text. docs/reference/knobs.md is GENERATED from this table
// (`blade test knobs --write`; `blade test knobs` fails when the committed
// file is out of date), and the `knobs` test block also checks the table
// against the hand-written notes (docs/reference/knob-notes.md) and the hand-kept lists
// (`Optimize.optimizerGates`, `Optimize.optimizerGateMirrors`,
// `CliSelfTests.suiteClearedKnobs` / `suitePinnedKnobs`,
// `Setup.persistedToolchainKeys`).
//
// Below the table: the shared PARSERS (the switch grammar, the BLADE_TILE_TUNE
// key=value list, integers, cache directories) and the READ functions. The
// registry holds NO state and caches NOTHING: every read is a call that reads
// the process environment at that moment (CLAUDE.md: gates are functions read
// per call, so a harness may pin and restore a knob mid-process). The
// toolchain-file half of a mirrored knob (env first, then the file) stays in
// Toolchain.fs, which owns that file's parse cache.
//
// Compiled early (right after Runtime.fs): every file that reads a knob can
// name its entry. Not every reader goes through the registry yet -- the sites
// in the files a cleanup lane could not edit (Optimize*.fs, CodeGen*.fs,
// EmitLlvm.fs, IRMono.fs, CliSelfTests.fs, ...) still call
// `Environment.GetEnvironmentVariable` or `Toolchain.envSwitch` with the name
// spelled out; the K16 report lists each with the entry it should use.
module Blade.Knobs

open System

/// What a knob is for.
type Kind =
    /// In `Optimize.optimizerGates`: `blade test opt-diff` turns it off for its
    /// reference lane, and `blade test` clears it.
    | OptimizerGate
    /// An emission-only A/B arm outside the optimizer gates (a tile form, a
    /// fold form, a scatter): picks one of two equal-valued emissions.
    | AbArm
    /// A build or arithmetic policy (FP association and contraction, the BLAS
    /// route, threads, pool retention, the back end, a resource grant).
    | Policy
    /// Which top-level bindings print, and in which spelling.
    | EmissionForm
    /// Reports and records: no value, schedule or store changes.
    | Instrumentation
    /// Where tools, caches, libraries and the stdlib live.
    | Infrastructure
    /// Read by the test harness only.
    | TestHarness

/// Who reads the variable.
type Reader =
    /// The compiler process (front end, optimizer, emitters, interpreter, CLI).
    | Compiler
    /// The compiled program, at run time.
    | CompiledProgram
    /// The test harness (`blade test`).
    | Harness

/// What `blade test` does with the knob for its run (`withSuiteEnvironment`).
type SuiteAction =
    /// Left as the caller set it.
    | Untouched
    /// Unset for the run (`suiteClearedKnobs`).
    | Cleared
    /// Set to this value for the run (`suitePinnedKnobs`), not cleared.
    | Pinned of string
    /// In both lists: cleared, then set to this value.
    | ClearedAndPinned of string

/// Whether blade.toolchain.json carries the key.
type ToolchainMirror =
    /// Environment only.
    | EnvOnly
    /// Read from the toolchain file when the variable is unset or empty
    /// (env wins); not written by `blade setup`.
    | FileFallback
    /// Read from the toolchain file when unset, AND persisted from the
    /// environment by bare `blade setup` (`Setup.persistedToolchainKeys`).
    | Persisted

/// One accepted value (or a family of them, `<path>`) and what it means.
type Value =
    { Spell: string
      Means: string
      /// An `Optimize.optimizerGateMirrors` arm: `blade test opt-diff` compares
      /// this value against the default. `Spell` is then the exact value.
      Mirror: bool }

type Knob =
    { Name: string
      Kind: Kind
      Readers: Reader list
      /// The behaviour when the variable is unset, in words.
      Default: string
      Values: Value list
      Suite: SuiteAction
      Toolchain: ToolchainMirror
      /// Named in docs/reference/knob-notes.md. `false` marks a knob the notes do not
      /// mention yet (the `knobs` block checks the flag both ways).
      Documented: bool
      /// One line, under 160 characters.
      Text: string }

// ---------------------------------------------------------------------------
// Construction helpers (private: the table below is the only user).
// ---------------------------------------------------------------------------

let private v spell means = { Spell = spell; Means = means; Mirror = false }
let private mirror spell means = { Spell = spell; Means = means; Mirror = true }

let private knob name kind readers dflt values suite toolchain documented text =
    { Name = name; Kind = kind; Readers = readers; Default = dflt; Values = values
      Suite = suite; Toolchain = toolchain; Documented = documented; Text = text }

/// An optimizer gate that is ON by default (`unset` / `1` / `on`), off at
/// `0` / `off`; extra values (variants, mirror arms) follow.
let private gateOn name extra text =
    knob name OptimizerGate [ Compiler ] "on"
        ([ v "1 / on" "on (the default)"; v "0 / off" "off; `blade plan` records `declined -- disabled by` the knob" ] @ extra)
        Cleared EnvOnly true text

/// An optimizer gate that is OFF by default (an opt-in arm).
let private gateOptIn name values text =
    knob name OptimizerGate [ Compiler ] "off (opt-in)" values Cleared EnvOnly true text

/// An emission A/B arm, on by default, cleared by `blade test`.
let private armOn name extra text =
    knob name AbArm [ Compiler ] "on"
        ([ v "1 / on" "on (the default)"; v "0 / off" "the previous form, for A/B" ] @ extra)
        Cleared EnvOnly true text

// ---------------------------------------------------------------------------
// The table. Each entry is a named value (the read sites name it); `all` lists
// them in the order of the notes table (docs/reference/knob-notes.md), the undocumented ones last.
// ---------------------------------------------------------------------------

// ---- the BLAS / LAPACK route ----
let openblasDir =
    knob "OPENBLAS_DIR" Infrastructure [ Compiler ] "unset"
        [ v "<dir>" "the BLAS/LAPACK install root; its presence enables the BLAS route when BLADE_BLAS is unset" ]
        Cleared Persisted true
        "BLAS/LAPACK install root; its presence alone turns the BLAS route on when BLADE_BLAS is unset."
let blas =
    knob "BLADE_BLAS" Policy [ Compiler ] "unset: defers to OPENBLAS_DIR (on when it is set, else off)"
        [ v "1 / on" "force BLAS routing"; v "0 / off" "force it off" ]
        (ClearedAndPinned "off") Persisted true
        "Forces the BLAS/LAPACK route on or off; unset defers to OPENBLAS_DIR, and the route is off when neither is set."
let blasPrefer =
    knob "BLADE_BLAS_PREFER" Policy [ Compiler ] "auto"
        [ v "auto" "the native kernel, unless the library is identified as MKL (BLADE_BLAS_FLAVOR=mkl), then the library"
          v "native" "the native kernel where eligible"
          v "library / blas" "the library for every shape it serves"
          v "<other>" "reads as auto" ]
        (ClearedAndPinned "native") Persisted true
        "Who serves a contraction both a native Blade kernel (the packed syrk tile) and the BLAS library can serve."
let blasFlavor =
    knob "BLADE_BLAS_FLAVOR" Infrastructure [ Compiler ] "unset"
        [ v "mkl" "the library is MKL: its headers, and the library preferred under BLADE_BLAS_PREFER=auto" ]
        Cleared Persisted true
        "Identifies the BLAS library; `mkl` selects the MKL headers and makes the library the auto preference."
let cublas =
    knob "BLADE_CUBLAS" Policy [ Compiler ] "off (does not follow GPU presence)"
        [ v "1 / on" "route L3 contractions (gram, matmul) to cuBLAS"; v "0 / off" "off" ]
        Cleared EnvOnly true
        "Routes L3 contractions (gram, matmul) to cuBLAS; strictly opt-in."

// ---- build profile, threads, arithmetic ----
let memcheck =
    knob "BLADE_MEMCHECK" Policy [ Compiler ] "off"
        [ v "1 / on" "the ASan-instrumented Debug profile (clang64 preferred, MSVC fallback)"; v "0 / off" "the release profile" ]
        Untouched EnvOnly true
        "The ASan-instrumented Debug build profile (clang64 preferred, MSVC fallback); `blade run --memcheck` sets it."
let ompThreads =
    knob "BLADE_OMP_THREADS" Policy [ Compiler ] "thread pragmas emitted"
        [ v "1 / 0 / off / false" "suppresses OpenMP thread-pragma emission (`1` is the count one)"
          v "<other>" "emission stays on (`on`, `true`, `4`, ...)" ]
        Cleared EnvOnly true
        "Suppresses OpenMP thread-pragma emission at 1/0/off/false; the run-time thread count is plain OMP_NUM_THREADS."
let fpRepro =
    knob "BLADE_FP_REPRO" Policy [ Compiler ] "off: licensed folds take their fastest form"
        [ v "1 / on" "the reproducible build: every fold keeps the ascending serial association the interpreter evaluates" ]
        Cleared EnvOnly true
        "The reproducible build: every fold keeps the ascending serial association the interpreter evaluates."
let fpReassoc =
    knob "BLADE_FP_REASSOC" Policy [ Compiler ] "on: the licensed fast fold forms"
        [ v "0 / off" "the compatibility spelling of BLADE_FP_REPRO=1"
          v "1" "explicit: also lets the Reynolds term merger flatten float `+` / `*` chains (corpus reynolds/026)" ]
        Cleared EnvOnly true
        "Default on; 0/off is the compatibility spelling of BLADE_FP_REPRO=1, and an explicit 1 lets the Reynolds merger flatten float chains."

// ---- AD ----
let adHaloGather =
    knob "BLADE_AD_HALO_GATHER" AbArm [ Compiler ] "the gather adjoint"
        [ v "1 / on" "keep the halo map and emit the gather adjoint (the default)"; v "0 / off" "lower it into the construction loop and scatter" ]
        Cleared EnvOnly true
        "The reverse-mode rule of a halo stencil map: the gather adjoint, or (0/off) lowered into the construction loop and scattered."
let adGatherFuse =
    gateOn "BLADE_AD_GATHER_FUSE" []
        "Runs a sole-halo stencil's gather adjoint taps as ONE loop over the input ordinal instead of one pass per tap."
let adCheckpoint =
    gateOptIn "BLADE_AD_CHECKPOINT"
        [ v "1 / on / auto" "checkpoint a tape over 4096 cells, K = ceil(sqrt(T - 1))"
          v "0 / off" "the full tape"
          v "<k>" "an integer k >= 2: a checkpoint every k steps for any recurrence longer than k"
          mirror "3" "a checkpoint every 3 steps (the opt-diff mirror arm)" ]
        "Opt-in checkpointed reverse sweep of a differentiated rank-1 let rec: segment checkpoints and replay instead of the full tape."
let adReplayPrune =
    gateOptIn "BLADE_AD_REPLAY_PRUNE"
        [ mirror "1" "prune (`1` / `on`)"; v "0 / off" "the whole replay" ]
        "Opt-in dead-replay pruning in the reverse sweep: drops replayed loop-local lets and fresh-local writes nothing reads."

// ---- the optimizer gates ----
let blockFloat =
    gateOn "BLADE_BLOCK_FLOAT" []
        "Floats an addressed-draw block's compute onto the body spine, so compute elision can fuse the draw into its fold."
let fusion =
    gateOn "BLADE_FUSION" []
        "Elementwise-chain fusion and its extensions (slice-write fusion, row-fold fusion, fold state in place)."
let freezeIdiom =
    gateOn "BLADE_FREEZE_IDIOM" [] "Freeze-idiom recognition."
let cse =
    gateOn "BLADE_CSE" [] "Let-level common-subexpression elimination."
let poolReuse =
    gateOn "BLADE_POOL_REUSE" [ mirror "literal" "the literal-only scratch reuse planner" ]
        "Scratch pool reuse: same-cell returns and pools whose run-time extents are proved equal share storage."
let poolEarlyRelease =
    gateOn "BLADE_POOL_EARLY_RELEASE" []
        "Early pool release: a scratch pool is released right after its last use instead of at scope exit."
let hoist =
    gateOn "BLADE_HOIST" []
        "Kernel-invariant hoisting: an invariant subexpression or array call is evaluated once before the nest."
let hotRow =
    gateOn "BLADE_HOT_ROW" []
        "Hot-row fold scheduling: a later row fold runs right behind each row inside the producer's outer loop."
let cellEpilogue =
    gateOn "BLADE_CELL_EPILOGUE" [ v "lanes" "reserved for the licensed row arm (B2l, not built): on plus a recorded decline" ]
        "Cell epilogues: a scalar consumer fold runs inside its producer's cell loop instead of re-reading the stored output."
let compactionFusion =
    gateOn "BLADE_COMPACTION_FUSION"
        [ mirror "b4" "the exact round-2 form (positions always built)"
          mirror "gather" "v2 plus the opt-in mask-site gather"
          mirror "unshared" "the pre-T4c v2: one selection index per compound" ]
        "Compaction fusion: mask and compound in one branchless pass, the kept-position table elided when the census allows."
let compactRowClasses =
    gateOn "BLADE_COMPACT_ROW_CLASSES" []
        "Compact row classes: each packed row's orbit-class partials are stored right after the row; no second pass."
let siblingFolds =
    gateOn "BLADE_SIBLING_FOLDS"
        [ mirror "strict" "join every leg in ONE strict traversal instead of splitting by licence"
          mirror "top" "also join the top-level binding sequence" ]
        "Sibling scalar folds over one axis become one reduction-join traversal."
let recStepOmp =
    gateOn "BLADE_REC_STEP_OMP" []
        "The threaded let rec step: a `where omp` final map runs the whole time loop on one thread team."
let memberHoist =
    gateOn "BLADE_MEMBER_HOIST" [ v "linear / bitmap / hash" "force that search structure, for A/B" ]
        "Membership hoisting: per-cell contains(B, x) probes read one search structure built over B before the host."
let hashJoin =
    gateOn "BLADE_HASH_JOIN" []
        "The hash equijoin: a stable CSR over the right keys instead of the six as-written passes over all pairs."
let joinSelection =
    gateOn "BLADE_JOIN_SELECTION" []
        "The hash join's census certifies a selection (mask + compound) over its relation as a buffer/cardinality reader."
let streamGroupAgg =
    gateOptIn "BLADE_STREAM_GROUP_AGG"
        [ mirror "1" "the state form (`1` / `on`)"; v "0 / off" "the windowed scatter" ]
        "Opt-in: per-group folds of a streamed key grouping keep one state per group instead of storing the grouped values."
let epilogueLegs =
    gateOn "BLADE_EPILOGUE_LEGS" [ mirror "1" "also retain the kept one-level inline (warns BL7005)" ]
        "Producer-input legs: sibling folds over a producer's inputs ride its loop; an explicit 1 also keeps the inline."
let groupAgg =
    gateOn "BLADE_GROUP_AGG" [ v "licensed" "experimental: a licensed fold takes the ascending chain (an association change)" ]
        "Aggregate-only grouping: one pass over the rows in a function body instead of the CSR, gather and peels."
let computeElide =
    gateOn "BLADE_COMPUTE_ELIDE" [ mirror "chain" "also the deferred chain to fold" ]
        "Compute elision: a single-consumer `|> compute` let is fused into its one reader instead of stored."
let gatherElide =
    gateOn "BLADE_GATHER_ELIDE" []
        "The gather terminal: a gather consumed by the adjacent fold is not stored."
let decompactFold =
    gateOn "BLADE_DECOMPACT_FOLD"
        [ mirror "top" "the top-level arms only"; v "body" "reads as on" ]
        "A fold or mat-vec over decompact(S) walks the packed pool by rows instead of building the dense n x n buffer."
let sortForm =
    gateOn "BLADE_SORT_FORM" []
        "Sort forms: an identity-source sort of a range sorts in place, and a total key is cached once per element."
let joinAgg =
    gateOn "BLADE_JOIN_AGG" []
        "Join to aggregate: a non-printed join relation read only by folds is never stored; each fold walks the probe."
let luCompanion =
    gateOn "BLADE_LU_COMPANION" []
        "The LU column-major companion: m.lu keeps LAPACK's factor, so each top-level lu_solve skips the per-solve copy."
let foldMapElide =
    gateOn "BLADE_FOLD_MAP_ELIDE" []
        "Fold-of-map elision: a computed let read only at operand positions of one fold's map is not stored."
let siblingMaps =
    gateOn "BLADE_SIBLING_MAPS"
        [ mirror "nostream" "resident groups only (the streamed halo kind declined)"; v "streamed" "reads as on" ]
        "Sibling maps: two to eight stored maps over one input index space share one traversal."
let joinProducer =
    gateOn "BLADE_JOIN_PRODUCER" []
        "The join producer: a producer read only by one reduction join is evaluated once per coordinate, not stored."
let streamGroupFold =
    gateOn "BLADE_STREAM_GROUP_FOLD" []
        "Per-group folds of a streamed structural grouping fold each run in one window; no pool of all N cells."
let streamMaskReplay =
    gateOn "BLADE_STREAM_MASK_REPLAY" []
        "A streamed mask read only by its compound is counted, not stored; the gather re-evaluates the predicate."
let sortTerminal =
    gateOn "BLADE_SORT_TERMINAL" []
        "Under the spool grant, a sort read only by one fold feeds the fold as it emits; no sorted output is stored."
let streamFoldFuse =
    gateOn "BLADE_STREAM_FOLD_FUSE" []
        "A fold over a map of one streamed variable folds per block of the store's sweep, with no N-cell buffer."
let keyedProjection =
    gateOn "BLADE_KEYED_PROJECTION" []
        "A Zarr read used only at literal keys keeps its whole sweep but retains only the demanded cells."
let transposeElide =
    gateOn "BLADE_TRANSPOSE_ELIDE" []
        "The transpose normal form: chains compose to one permutation, identities vanish, read-only aliases share storage."
let callElide =
    gateOn "BLADE_CALL_ELIDE" []
        "Call elision: a direct call folded or mapped once is jointly lowered at that site; no return pool."
let recompute =
    gateOn "BLADE_RECOMPUTE" []
        "Recompute selection: a cheap map read by two to eight consumers is recomputed when the cost model proves it dominant."
let recWindow =
    gateOn "BLADE_REC_WINDOW" []
        "Recursive-array windowing: a block or function-body let rec read at fixed rows is a K+1-row ring plus snapshots."
let groupView =
    gateOn "BLADE_GROUP_VIEW" []
        "The structural group view: a structural group_by's rows point into the source pool, and ungroup shares it."
let groupRowmap =
    gateOn "BLADE_GROUP_ROWMAP" []
        "A top-level ungroup of a per-group map over a structural grouping shares the map's pool instead of copying it."
let sinkAlias =
    gateOn "BLADE_SINK_ALIAS" []
        "Provider sink destination passing: a packed source is written from its pool in place, with no flatten copy."
let sinkStream =
    gateOn "BLADE_SINK_STREAM" [ mirror "always" "stream every admitted shape (the peak-memory arm)" ]
        "The streamed provider sink: a map read only by one Zarr write is produced block by block at the write."
let assemblyDest =
    gateOn "BLADE_ASSEMBLY_DEST" []
        "Assembly destination fill: a transpose / stack / join of a private cellwise map writes its destination once."
let modeProductDest =
    gateOptIn "BLADE_MODE_PRODUCT_DEST"
        [ mirror "1" "in place (`1` / `on`)"; v "0 / off" "the flat work array and its reshape" ]
        "Opt-in: m.mode_product fills its rank-N result in place instead of a flat work array and its reshape."
let haloCache =
    gateOn "BLADE_HALO_CACHE" [ mirror "stencil" "the certified stencil route only" ]
        "The halo chunk cache: each chunk of a streamed Zarr axis is fetched exactly once per evaluation on the certified routes."
let foldState =
    gateOn "BLADE_FOLD_STATE" []
        "The leading-axis fold's state in place: a pointwise kernel updates one private state array per step."
let recReplay =
    gateOn "BLADE_REC_REPLAY" []
        "Module-scope ring and print replay: a top-level let rec read at fixed rows is swept twice instead of stored."
let recFold =
    gateOn "BLADE_REC_FOLD" []
        "Folds riding a windowed recurrence: a let rec read only by folds is a ring, each fold advancing per row."
let orbitActions =
    gateOn "BLADE_ORBIT_ACTIONS" [ v "strict" "strict builds only (a licensed build keeps the licensed walk)" ]
        "The rank-2 row orbit action: one pass over the canonical cells feeds every weighted row chain."

// ---- the emission A/B arms ----
let syrkTile =
    armOn "BLADE_SYRK_TILE" []
        "The packed-triangle 6x8 register tile for the same-array comm row contraction and real-f64 gram(A, A)."
let antisymTile =
    armOn "BLADE_ANTISYM_TILE" [ v "6x8" "the sequential 6x8 tile (the default)"; v "4x4 / paired" "the paired 4x4 tile" ]
        "The antisym pair contraction's packed tile: the sequential 6x8 (default), the paired 4x4, or the plain nest."
let simplexTile =
    armOn "BLADE_SIMPLEX_TILE" []
        "The rank-3 simplex comm contraction's 6x8 tile with the W panel; 0/off is the plain per-cell nest."
let tileTune =
    knob "BLADE_TILE_TUNE" AbArm [ Compiler ] "unset: each key's own default"
        [ v "syrk_g=<n>" "syrk tile row panels per group (1..8, default 4)"
          v "anti_kc=<n>" "antisym sample-block cap (default 0: one block)"
          v "anti_g=<n>" "antisym row panels per group (default per regime)"
          v "sel_cap=<KiB>" "compaction fusion's gather selectivity cap (default 262144)"
          v "tri_edge=<0|1>" "syrk / simplex edge tiles: live accumulators only (1, default) or the full kloop (0)"
          v "rowslots_min=<n>" "the complete-row form's static work gate (default 262144)"
          v "pull_min=<n>" "the rank-3 row-owned pull's work gate (default 262144)"
          v "epi_blk=<n>" "the cell epilogue's producer block (default 256)"
          v "epi_blk_r=<n>" "the cell-chain mini-block (default 0: one fused loop)"
          v "scatter_g=<n>" "the ordered orbit scatter's row group (1..8, default 4)"
          v "scatter_b=<n>" "its column block (8..1024, default 64)"
          v "scatter_vrow=<0|1>" "the LLVM lane's vector row chains (default 1)"
          v "group_win=<n>" "the streamed key grouping's window (default 65536)" ]
        Cleared EnvOnly true
        "Speed-only key=value parameters of the packed-tile drivers, cell epilogues and scatters; no bit depends on them."
let orbitFold =
    armOn "BLADE_ORBIT_FOLD" []
        "A licensed (+) fold over a compact array's logical cells is one orbit-class pass over the packed pool."
let domainDiagonal =
    armOn "BLADE_DOMAIN_DIAGONAL" []
        "A licensed fold over a band domain walks it by diagonal in unit-stride runs; 0/off is the row-major nest."
let foldLanes =
    armOn "BLADE_FOLD_LANES" [ v "4 / 8" "force that lane count K (default 8)" ]
        "The explicit K-lane vector form of a licensed dot-class fold; 0/off is the single-accumulator form."
let foldRowSlots =
    knob "BLADE_FOLD_ROW_SLOTS" Policy [ Compiler ] "on, yielding to the ordered orbit scatter where both admit a walk"
        [ v "1 / on / true" "on (the default)"
          v "prefer" "the row slots win over the ordered orbit scatter"
          v "0 / off / false" "no row slots"
          v "<other>" "reads as the default" ]
        Cleared EnvOnly true
        "The repro build's thread form for a `where omp` fold: complete row folds into indexed slots on a team."
let orbitScatter =
    armOn "BLADE_ORBIT_SCATTER" []
        "The ordered orbit scatter: a strict logical walk over a packed SymIdx pool is one pass feeding every chain in order."
let orbitPull =
    armOn "BLADE_ORBIT_PULL" []
        "The row-owned pull form of the rank-3 ordered orbit scatter on a thread team, in strict threaded builds."
let joinThreads =
    armOn "BLADE_JOIN_THREADS" []
        "The threaded reduction join: an explicitly requested, fully licensed `<&!>` join opens one Path B team."

// ---- compiler flags ----
let march =
    knob "BLADE_MARCH" Policy [ Compiler ] "native"
        [ v "off" "no -march flag (trimmed, any case)"; v "<cpu>" "-march=<cpu>, trimmed" ]
        Cleared EnvOnly true
        "The g++ (and clang) -march= value; off drops the flag."
let fpContract =
    knob "BLADE_FP_CONTRACT" Policy [ Compiler ] "fast"
        [ v "off" "no contraction: the per-build reproducible mode"; v "<mode>" "-ffp-contract=<mode>, trimmed" ]
        Cleared EnvOnly true
        "The g++ -ffp-contract= mode (default fast); off is the per-build reproducible mode; the LLVM lane follows the same default."

// ---- paths, printing, records ----
let stdlib =
    knob "BLADE_STDLIB" Infrastructure [ Compiler ] "unset: the checkout's stdlib/, then the deployed copy"
        [ v "<dir>" "an extra stdlib search root" ]
        Untouched EnvOnly true
        "An extra stdlib search root."
let printSelection =
    knob "BLADE_PRINT" EmissionForm [ Compiler ] "every top-level binding"
        [ v "<names>" "only these bindings (comma or space separated; an imported binding qualified, `Lib.y`)" ]
        Cleared EnvOnly true
        "Which top-level bindings the program prints; set by `--print a,b` on any verb, read by both lanes."
let printForm =
    knob "BLADE_PRINT_FORM" EmissionForm [ Compiler ] "test in-process; display for run / compile / emit / plan"
        [ v "test" "the spelling every pin, doc fence and differential gate reads"
          v "display" "for people: shortest round-trip floats, units, unlabelled bare expressions" ]
        (Pinned "test") FileFallback true
        "Which spelling prints use: the test form or the display form; `--print-form` wins over it."
let printBits =
    knob "BLADE_PRINT_BITS" Instrumentation [ Compiler; CompiledProgram ] "off"
        [ v "1 / on" "append each printed float's raw IEEE-754 bits (` #<hex>`) after its decimal text" ]
        Cleared EnvOnly true
        "A run-time test instrument: each printed float's raw IEEE-754 bit pattern after its decimal text."
let tileCache =
    knob "BLADE_TILE_CACHE" Policy [ Compiler; CompiledProgram ] "off"
        [ v "1 / on" "the store %LOCALAPPDATA%\\Blade\\tile-cache"; v "<absolute path>" "that store"; v "0 / off" "off" ]
        Cleared EnvOnly true
        "Revision reuse of tiled bindings (structural/04): the compiler plans them and the program probes, loads and stores."
let tileCacheVerbose =
    knob "BLADE_TILE_CACHE_VERBOSE" Instrumentation [ Compiler; CompiledProgram ] "off"
        [ v "1" "print the planner's admissions and the run's [tiles] / [chunks] census" ]
        Cleared EnvOnly true
        "Prints the tile planner's admissions and the run's [tiles] / [chunks] census."
let runRecord =
    knob "BLADE_RUN_RECORD" Instrumentation [ CompiledProgram ] "unset: no record"
        [ v "<path>" "write the JSON run record there at exit" ]
        Cleared EnvOnly true
        "The path the compiled program writes its JSON run record to at exit; `blade run --run-record` sets it."
let poolRetain =
    knob "BLADE_POOL_RETAIN" Policy [ Compiler ] "default: 32 MB blocks / 64 MB budget"
        [ v "default" "the default"
          v "0 / off / false" "the pre-wave-6 limits, 4 MB / 8 MB"
          v "1 / on / true" "the probe arm, 64 MB / 96 MB"
          v "<maxMB>:<budgetMB>" "an explicit pair, 0 < max <= budget <= 1048576"
          v "<other>" "the default, with a warning" ]
        (ClearedAndPinned "default") Persisted true
        "The runtime allocator's pool-retention policy: the largest retained block and the retained-bytes budget."
let ownedMove =
    armOn "BLADE_OWNED_MOVE" []
        "The owned move: a whole-array reassignment of an owning binding frees its previous pool and takes the new one."
let workspaceCensus =
    knob "BLADE_WORKSPACE_CENSUS" Instrumentation [ Compiler ] "off: no census code"
        [ v "1 / on" "count per-worker scratch of threaded sites into the run record's `workspace` member" ]
        Cleared EnvOnly true
        "Compile-time instrumentation: counts per-worker scratch of threaded sites into the run record."
let privateSpool =
    knob "BLADE_PRIVATE_SPOOL" Policy [ Compiler ] "no grant"
        [ v "root=<dir>,mem=<size>,disk=<size>[,resident=<size>]" "the grant: scratch root, memory, disk and resident budgets"
          v "0 / off / false" "no grant"
          v "<other>" "a hard failure of the verb" ]
        Cleared EnvOnly true
        "The private statistical-barrier spool grant (root, memory and disk budgets); environment only, never persisted."
let llvm =
    knob "BLADE_LLVM" Policy [ Compiler ] "off"
        [ v "1 / on" "run / compile / emit try the direct LLVM back end first, falling back to C++" ]
        Cleared EnvOnly true
        "Tries the direct LLVM back end first for run / compile / emit, falling back to the C++ lane when it declines."
let llvmClang =
    knob "BLADE_LLVM_CLANG" Infrastructure [ Compiler ] "clang on PATH, then C:\\msys64\\clang64\\bin\\clang.exe"
        [ v "<path>" "this clang, used verbatim (trimmed)" ]
        Untouched EnvOnly true
        "The clang the LLVM lane uses, verbatim: a bad value fails loudly instead of being replaced."
let exeCache =
    knob "BLADE_EXE_CACHE" Infrastructure [ Compiler ] "on: %LOCALAPPDATA%\\Blade\\exe-cache"
        [ v "1 / on" "the default directory"; v "0 / off" "disabled (`--no-cache`)"; v "<absolute path>" "that directory"
          v "<relative path>" "disabled" ]
        Untouched EnvOnly true
        "The content-addressed executable cache, keyed on the emitted text and the CPU -march=native resolves to."
let pch =
    knob "BLADE_PCH" Infrastructure [ Compiler ] "on: %LOCALAPPDATA%\\Blade\\pch-cache"
        [ v "1 / on" "the default directory"; v "0 / off" "disabled"; v "<absolute path>" "that directory" ]
        Untouched EnvOnly true
        "The precompiled prelude: a generated program's leading include block is compiled once per flag set."
let toolchainFile =
    knob "BLADE_TOOLCHAIN_FILE" Infrastructure [ Compiler ] "blade.toolchain.json beside the executable"
        [ v "<path>" "that file" ]
        Untouched EnvOnly true
        "The blade.toolchain.json consulted; written by `blade setup`, its keys mirror the environment variables."
let perfCounters =
    knob "BLADE_PERF_COUNTERS" Instrumentation [ Compiler ] "off"
        [ v "1" "[perf] counters on stderr" ]
        Untouched EnvOnly true
        "Compiler self-profiling: [perf] counters on stderr."
let phaseTiming =
    knob "BLADE_PHASE_TIMING" Instrumentation [ Compiler ] "off"
        [ v "1" "[phase] timings on stderr" ]
        Untouched EnvOnly true
        "Compiler self-profiling: [phase] timings on stderr."
let interpTimeoutMs =
    knob "BLADE_INTERP_TIMEOUT_MS" TestHarness [ Harness ] "300000"
        [ v "<ms>" "a positive per-test ceiling; a malformed value is the default" ]
        Untouched EnvOnly true
        "The per-test ceiling of the interpreter differential, in milliseconds."
let interpJobs =
    knob "BLADE_INTERP_JOBS" TestHarness [ Harness ] "the processor count"
        [ v "<n>" "n >= 1 tests prepared ahead"; v "1" "one test at a time (the A/B arm)" ]
        Untouched EnvOnly true
        "How many tests' compiled side the interpreter differential prepares ahead of the test it judges."
let interpReading =
    knob "BLADE_INTERP_READING" TestHarness [ Compiler ] "unset: the interpreter's own forcing schedule, unchanged"
        [ v "binding" "reading A: an uncomputed array binding is evaluated at its binding and kept"
          v "use" "reading B: it is evaluated afresh at each use from the current inputs, never kept across a write"
          v "<other>" "reads as unset" ]
        Cleared EnvOnly true
        "Test instrument (borrow oracle): which evaluation reading the interpreter gives an uncomputed array binding; set alone, those bindings do not print."
let netcdfDir =
    knob "NETCDF_DIR" Infrastructure [ Compiler ] "unset: bare -lnetcdf"
        [ v "<dir>" "the NetCDF include / link root" ]
        Untouched Persisted true
        "The NetCDF provider's include / link root."
let memberVerbose =
    knob "BLADE_MEMBER_VERBOSE" Instrumentation [ CompiledProgram ] "off"
        [ v "1" "a [member] line per membership build on stderr" ]
        Cleared EnvOnly true
        "A run-time report of each membership build ([member] lines on stderr); changes no emission."
let joinReport =
    knob "BLADE_JOIN_REPORT" Instrumentation [ Compiler ] "off: no report code"
        [ v "1 / on" "the C++ lane emits a [join] line after each hash join's counting pass" ]
        Cleared EnvOnly true
        "Compile-time: the C++ lane emits a [join] report line after each hash join's counting pass."

// ---- no notes yet (Documented = false) ----
let blasLink =
    knob "BLADE_BLAS_LINK" Infrastructure [ Compiler ] "unset"
        [ v "<flags>" "the explicit tier's link flags (MKL, BLIS); their presence selects that tier" ]
        Cleared Persisted false
        "The explicit BLAS tier's link flags (MKL, BLIS); setting it selects that tier."
let blasInclude =
    knob "BLADE_BLAS_INCLUDE" Infrastructure [ Compiler ] "unset"
        [ v "<dirs>" "the explicit tier's include directories" ]
        Cleared Persisted false
        "The explicit BLAS tier's include directories."
let lapackLink =
    knob "BLADE_LAPACK_LINK" Infrastructure [ Compiler ] "unset: no LAPACK on the explicit tier"
        [ v "<flags>" "the explicit tier's LAPACK link flags" ]
        Cleared Persisted false
        "The explicit BLAS tier's LAPACK link flags."
let lapackInclude =
    knob "BLADE_LAPACK_INCLUDE" Infrastructure [ Compiler ] "unset"
        [ v "<dirs>" "the explicit tier's LAPACK include directories" ]
        Cleared Persisted false
        "The explicit BLAS tier's LAPACK include directories."
let msmpiBin =
    knob "MSMPI_BIN" Infrastructure [ Compiler ] "unset: mpiexec on PATH"
        [ v "<dir>" "the MS-MPI bin directory holding mpiexec" ]
        Untouched Persisted false
        "The MS-MPI bin directory `--mpi` runs mpiexec from."
let llvmBricks =
    knob "BLADE_LLVM_BRICKS" AbArm [ Compiler ] "auto: the derived policy"
        [ v "on / auto / 1" "the derived policy"; v "off / 0 / none" "no bricks"; v "<B>" "a fixed tile edge B" ]
        Cleared EnvOnly false
        "The LLVM lane's brick (tile edge) policy: derived, off, or a fixed edge."
let llvmFacts =
    knob "BLADE_LLVM_FACTS" AbArm [ Compiler ] "every fact class emits"
        [ v "off / 0 / none" "no class emits"; v "<class>:off" "that class does not emit" ]
        Cleared EnvOnly false
        "A bisection knob of the LLVM lane: which classes of emitted facts (fast-math flags, ...) are on."
let llvmDumpIr =
    knob "BLADE_LLVM_DUMP_IR" Instrumentation [ Compiler ] "off"
        [ v "1" "dump the emitted LLVM IR" ]
        Untouched EnvOnly false
        "Dumps the LLVM lane's emitted IR."
let shapeSpecCap =
    knob "BLADE_SHAPE_SPEC_CAP" Policy [ Compiler ] "4"
        [ v "<n>" "at most n shape specializations per function (1..64); 0, negative or above 64 clamp to 64" ]
        Cleared EnvOnly false
        "The cap on shape specializations per function (default 4); the termination backstop of the worklist."
let debugShapeSpec =
    knob "BLADE_DEBUG_SHAPE_SPEC" Instrumentation [ Compiler ] "off"
        [ v "<anything but 0>" "print the per-module specialize / cap / decline census" ]
        Untouched EnvOnly false
        "Prints the per-module shape specialize / cap / decline census."
let gradDump =
    knob "BLADE_GRAD_DUMP" Instrumentation [ Compiler ] "off"
        [ v "<anything but 0>" "print each synthesized ad.grad / vjp / jvp declaration on stderr" ]
        Untouched EnvOnly false
        "Prints each synthesized ad.grad / ad.vjp / ad.jvp declaration (F# structural form) on stderr."
let memcheckCxx =
    knob "BLADE_MEMCHECK_CXX" Infrastructure [ Compiler ] "C:\\msys64\\clang64\\bin\\clang++.exe"
        [ v "<path>" "the clang++ the memcheck profile tries first" ]
        Untouched EnvOnly false
        "The clang++ the memcheck profile tries before the MSYS2 clang64 default."
let exeCacheVerbose =
    knob "BLADE_EXE_CACHE_VERBOSE" Instrumentation [ Compiler ] "off"
        [ v "1 / on" "[cache] hit / store tracing on stderr" ]
        Untouched EnvOnly false
        "Traces executable-cache hits and stores on stderr; `blade run --verbose` sets it."
let grRender =
    knob "BLADE_GR_RENDER" Infrastructure [ Compiler ] "the helper beside Blade.exe, then tools/gr-render above it"
        [ v "<path>" "this gr-render helper, trimmed; a path that does not exist is an error" ]
        Untouched EnvOnly false
        "The gr-render display helper to use instead of the deployed or checkout copy."
let llvmGoldenUpdate =
    knob "BLADE_LLVM_GOLDEN_UPDATE" TestHarness [ Harness ] "off: the golden pins are asserted"
        [ v "1" "rewrite the LLVM golden pins instead of asserting them" ]
        Untouched EnvOnly false
        "Rewrites the LLVM lane's golden emission pins instead of asserting them."
let testFsharpParallel =
    knob "BLADE_TEST_FSHARP_PARALLEL" TestHarness [ Harness ] "off: the F# pipeline runs under its lock"
        [ v "1 / on" "run the F# pipeline unlocked (an experiment)" ]
        Untouched EnvOnly false
        "Runs the test harness's F# pipeline unlocked (an experiment; the default stays locked)."

/// Every knob, in the order of the notes table, the undocumented ones
/// last.
let all : Knob list =
    [ openblasDir; blas; blasPrefer; cublas; memcheck; ompThreads; fpRepro; fpReassoc
      adHaloGather; adGatherFuse; adCheckpoint; adReplayPrune; blockFloat
      fusion; freezeIdiom; cse; poolReuse; hoist; hotRow; cellEpilogue; compactionFusion
      compactRowClasses; siblingFolds; recStepOmp; memberHoist; hashJoin; joinReport
      joinSelection; streamGroupAgg; epilogueLegs; groupAgg; computeElide; gatherElide
      decompactFold; sortForm; joinAgg; luCompanion; foldMapElide; siblingMaps
      joinProducer; streamGroupFold; streamMaskReplay; sortTerminal; streamFoldFuse
      keyedProjection; transposeElide; callElide; recompute; recWindow; groupView
      groupRowmap; sinkAlias; sinkStream; assemblyDest; modeProductDest; haloCache
      foldState; recReplay; recFold; syrkTile; antisymTile; simplexTile; tileTune
      orbitFold; domainDiagonal; foldLanes; foldRowSlots; orbitScatter; orbitPull
      orbitActions; joinThreads; march; fpContract; stdlib; printSelection; printForm; printBits
      tileCache; tileCacheVerbose; runRecord; poolRetain; ownedMove; workspaceCensus
      privateSpool; llvm; llvmClang; exeCache; pch; toolchainFile; perfCounters
      phaseTiming; interpTimeoutMs; interpJobs; interpReading; netcdfDir; blasFlavor; memberVerbose
      poolEarlyRelease
      blasLink; blasInclude; lapackLink; lapackInclude; msmpiBin; llvmBricks; llvmFacts
      llvmDumpIr; shapeSpecCap; debugShapeSpec; gradDump; memcheckCxx; exeCacheVerbose
      grRender; llvmGoldenUpdate; testFsharpParallel ]

/// The entry named `name`, if the registry has one.
let tryFind (name: string) : Knob option =
    all |> List.tryFind (fun k -> k.Name = name)

// ---------------------------------------------------------------------------
// The shared parsers. Pure: each takes the RAW value (`null` = unset), exactly
// as `Environment.GetEnvironmentVariable` returns it.
// ---------------------------------------------------------------------------

module Parse =
    /// The reading of an on/off switch: ONE grammar, trimmed and
    /// case-insensitive. `1` / `on` / `true` is on, `0` / `off` / `false` is
    /// off, unset or empty (or all blanks) is unset, anything else is `Other`
    /// with the trimmed text (for the knobs that also take a value).
    /// `Toolchain.Switch` is the same reading under its older case names.
    type Reading =
        | On
        | Off
        | Unset
        | Other of string

    /// Parse one switch value (`None` = unset). A `Some null` throws, as the
    /// grammar always has (no reader passes one).
    let switchOf (value: string option) : Reading =
        match value with
        | None -> Unset
        | Some v ->
            let t = v.Trim()
            match t.ToLowerInvariant() with
            | "" -> Unset
            | "1" | "on" | "true" -> On
            | "0" | "off" | "false" -> Off
            | _ -> Other t

    /// Parse a raw switch value (`null` = unset).
    let switch (raw: string) : Reading =
        switchOf (match raw with null -> None | v -> Some v)

    /// A boolean knob: on or off as spelled, else `defaultOn` -- an unset knob
    /// AND an unrecognized value both read as the default, so a typo never
    /// flips a default-off knob on (or a default-on pass off).
    let flag (raw: string) (defaultOn: bool) : bool =
        match switch raw with
        | On -> true
        | Off -> false
        | Unset | Other _ -> defaultOn

    /// One key of a BLADE_TILE_TUNE list: `key=value` pairs separated by commas,
    /// spaces or semicolons; the first pair whose key (trimmed, any case)
    /// matches AND whose value parses as an Int64 wins; unknown keys are
    /// ignored and an unset knob or a malformed value is `dflt`.
    let tileTune (key: string) (dflt: int64) (raw: string) : int64 =
        match raw with
        | null -> dflt
        | v ->
            v.Split([| ','; ' '; ';' |], StringSplitOptions.RemoveEmptyEntries)
            |> Array.tryPick (fun kv ->
                match kv.Split('=') with
                | [| k; x |] when k.Trim().ToLowerInvariant() = key ->
                    match Int64.TryParse(x.Trim()) with
                    | true, n -> Some n
                    | _ -> None
                | _ -> None)
            |> Option.defaultValue dflt

    /// An Int32 value, untrimmed (`Int32.TryParse`'s own rules); `None` when
    /// unset, empty or malformed. The integer knobs each add their own range
    /// rule on top.
    let int32 (raw: string) : int option =
        match raw with
        | null | "" -> None
        | s ->
            match Int32.TryParse s with
            | true, n -> Some n
            | _ -> None

    /// A cache-directory knob's setting.
    type Directory =
        /// The knob's default directory.
        | DefaultDirectory
        /// No directory: the cache is off.
        | NoDirectory
        /// This (rooted) directory, trimmed.
        | AtDirectory of string

    /// The grammar of the cache-directory knobs (BLADE_EXE_CACHE, BLADE_PCH,
    /// BLADE_TILE_CACHE): `1` / `on` = the default directory, `0` / `off` =
    /// none, a ROOTED path = that directory, any other text = none; unset is
    /// the default directory when `unsetIsDefault`, else none.
    let directory (unsetIsDefault: bool) (raw: string) : Directory =
        match switch raw with
        | Unset -> if unsetIsDefault then DefaultDirectory else NoDirectory
        | On -> DefaultDirectory
        | Off -> NoDirectory
        | Other t when IO.Path.IsPathRooted t -> AtDirectory t
        | Other _ -> NoDirectory

// ---------------------------------------------------------------------------
// The reads. Every one reads the process environment NOW.
// ---------------------------------------------------------------------------

/// The raw value of a variable named at run time (`null` when unset): the
/// generic helpers (`Toolchain.envSwitch name`, `Toolchain.getWithOrigin key`)
/// whose callers pass a name.
let rawNamed (name: string) : string =
    Environment.GetEnvironmentVariable name

/// The raw value of `k` (`null` when unset).
let raw (k: Knob) : string =
    Environment.GetEnvironmentVariable k.Name

/// `k` read as a switch.
let switch (k: Knob) : Parse.Reading =
    Parse.switch (raw k)

/// `k` read as a boolean with this default.
let flag (k: Knob) (defaultOn: bool) : bool =
    Parse.flag (raw k) defaultOn

/// `k` read as a cache directory (see `Parse.directory`).
let directory (k: Knob) (unsetIsDefault: bool) : Parse.Directory =
    Parse.directory unsetIsDefault (raw k)
