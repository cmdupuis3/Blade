// The direct LLVM back end (`BLADE_LLVM` lane): scalar programs plus DENSE
// STATIC-EXTENT arrays.
//
// docs/plans/plan-llvm-backend.md section 5 fixes the shape this implements:
// textual `.ll` written by F# and handed to `clang file.ll shim.o -O3` in ONE
// step (no opt/llc orchestration), opaque pointers only, and -- the decision
// that keeps this file small -- SSA BY ALLOCA. Every binding, parameter and
// match-arm variable gets an `alloca` plus loads and stores; clang's own -O3
// runs mem2reg/SROA and rebuilds real SSA from it. That is how clang itself
// emits C, and it deletes the dominance-frontier problem from the emitter, so
// there is no phi node anywhere below.
//
// REFUSAL IS WHOLE-PROGRAM. `tryEmitProgram` returns `Error <reason>` naming
// the first unsupported construct and emits NOTHING; the caller prints one
// notice and compiles the same program through the C++ lane instead. A
// half-emitted module is never produced, so the lane can only be all-correct
// or absent -- which is what makes it safe to develop against a byte-pinned
// corpus.
//
// THE BOUNDARY is Int64/Float64/Bool/String scalars, and arrays over
// `Idx<n>` axes whose extents are compile-time constants. Every array is a
// flat row-major pool (no Iliffe skeleton, no triangular packing) with the
// extents baked into its GEPs, and every UNFORCED computation -- a
// combinator, `A + B`, `range<I>` -- is a producer closure that never
// acquires storage until `|> compute` asks for it. Refused by name, each
// with its own wording: symmetric/antisymmetric (compact) storage, runtime
// extents, ragged/compound/sparse/orbit index types, providers, cuda/mpi,
// complex, `let rec` (IRForRange), structs and display frames.
//
// NUMERIC POLICY (plan section 6, polarity flipped 2026-10-17 --
// docs/plans/plan-fp-default-fastest.md): the DEFAULT emission is the fastest
// the source licenses, exactly as in the g++ lane. Two per-instruction flags:
// `reassoc nsz` on a fold whose kernel carries the SAME license the C++ lane's
// K-lane / `omp simd reduction` forms spend (`foldReorderLicensed`, in any
// build but BLADE_FP_REPRO=1), and `contract` on every fmul/fadd/fsub unless
// `BLADE_FP_CONTRACT` says otherwise (the g++ lane's `-ffp-contract=fast`
// default). Byte identity with the interpreter is the opt-in, as there:
// BLADE_FP_REPRO=1 BLADE_FP_CONTRACT=off. See the fact-layer section below.
//
// The printers this emits call into `src/cpp/blade_llvm_shim.c`, whose output
// bytes are pinned to the C++ lane's `cout << setprecision(15) << boolalpha`.
module Blade.EmitLlvm

open System
open System.Collections.Generic
open System.Text
open Blade.Types
open Blade.IR
open Blade.IRLoopStructure
open Blade.IRStorage

/// Raised anywhere in the walk to abandon the WHOLE program. Carries the
/// one-line reason `tryEmitProgram` hands back.
exception LlvmRefusal of string

let private refuse (msg: string) : 'a = raise (LlvmRefusal msg)

/// The DU case name of an IR node, for refusals that have nothing better to
/// say than "no arm for this". Reflection is fine: it only runs on the
/// refusal path, which ends the emission.
let private caseName (e: IRExpr) : string =
    let case, _ = Microsoft.FSharp.Reflection.FSharpValue.GetUnionFields(e, typeof<IRExpr>)
    case.Name

// ---------------------------------------------------------------------------
// The scalar universe
// ---------------------------------------------------------------------------

/// Every value v1 can hold. Deliberately NOT ElemType: this is the set the
/// emitter has a lowering for, and keeping it separate is what makes an
/// unsupported width (Int32/Float32/complex) a refusal instead of a silent
/// re-typing.
type private Sc =
    | ScI64
    | ScF64
    | ScBool
    | ScStr
    | ScVoid

let private llTy = function
    | ScI64 -> "i64"
    | ScF64 -> "double"
    | ScBool -> "i1"
    | ScStr -> "ptr"
    | ScVoid -> "void"

/// Project a Blade type onto the scalar universe. `IRTIdxTagged` (nominal
/// index tags) and `IRTUnitAnnotated` (physical units) are TRANSPARENT here
/// for the same reason they are at C++ codegen: both erase to their inner
/// type, so a `Float<mps>` binding is an ordinary double.
let rec private scalarTyOf (t: IRType) : Sc option =
    match t with
    | IRTScalar ETInt64 -> Some ScI64
    | IRTScalar ETFloat64 -> Some ScF64
    | IRTScalar ETBool -> Some ScBool
    | IRTScalar ETString -> Some ScStr
    | IRTScalar ETUnit | IRTUnit -> Some ScVoid
    | IRTIdxTagged (inner, _) -> scalarTyOf inner
    | IRTUnitAnnotated (inner, _) -> scalarTyOf inner
    | _ -> None

let private requireScalar (what: string) (t: IRType) : Sc =
    match scalarTyOf t with
    | Some sc -> sc
    | None ->
        refuse ($"{what} has type {(Blade.IRPrint.ppIRType t)} -- the llvm lane handles Int64/Float64/Bool/String scalars only")

// ---------------------------------------------------------------------------
// The fact layer (plan section 5)
//
// What the front end PROVED, serialized where stock -O3 passes read it:
// function attributes, parameter attributes, and per-instruction fast-math
// flags. Three rules govern everything below.
//
//   1. A FACT IS EMITTED ONLY WHERE IT IS PROVED. The red team's calibration
//      (plan section 8, risk 1: Rust shipped mutable-noalias in 2017,
//      regressed immediately, and it is still unstable six years on) is that
//      an alias fact asserted one notch beyond its proof is a miscompile, not
//      a slow program. So `noalias` appears on exactly one thing -- the
//      allocator's FRESH return pointer -- and never on a read operand, and
//      `readonly` is gated on a whole-module scan for element writes rather
//      than on a per-parameter guess.
//
//   2. FACTS ATTACH AT INSTRUCTION BIRTH, never by a later pass. Each builder
//      below takes its attribute text as a field, so there is no "decorate the
//      module afterwards" step that could decorate the wrong instruction.
//
//   3. ONE KILL SWITCH, so a miscompile bisects to a fact class in one run.
//      `BLADE_LLVM_FACTS=off` turns all of it off and returns the lane to the
//      shape that passed its differential before this layer existed;
//      `BLADE_LLVM_FACTS=fmf:off` (or `fnattrs:off`, `paramattrs:off`) turns
//      off one class.
//
// The record `ModuleFacts` is the seed of the backend-neutral `BackendFacts`
// the plan hands to a future EmitMlir: computed ONCE from the IR, consumed by
// serialization only. It lives here until a second backend needs it.
// ---------------------------------------------------------------------------

/// The three fact classes, as the kill switch spells them.
let private factFnAttrs = "fnattrs"
let private factParamAttrs = "paramattrs"
let private factFmf = "fmf"

/// Is this fact class licensed to emit? A FUNCTION, never a module-level
/// `let`, for the reason `Build.llvmEnabled` and `CodeGenState.fpReassocEnabled`
/// are: a harness that pins the variable mid-process must be honored by the
/// next emission.
///
/// `BLADE_LLVM_FACTS` unset (or set to anything without an `off` token) means
/// EVERY class emits -- the facts are the point of the lane. A bare `off`/`0`
/// disables all three; `<class>:off` disables one. An unrecognized token is
/// ignored rather than fatal: this is a bisection knob, and a typo that
/// silently disabled everything would make a bisection lie.
let factEnabled (cls: string) : bool =
    match System.Environment.GetEnvironmentVariable "BLADE_LLVM_FACTS" with
    | null | "" -> true
    | v ->
        let toks =
            v.Split([| ','; ';'; ' ' |], StringSplitOptions.RemoveEmptyEntries)
            |> Array.map (fun t -> t.Trim().ToLowerInvariant())
        if toks |> Array.exists (fun t -> t = "off" || t = "0" || t = "none") then false
        else
            let target = cls.ToLowerInvariant()
            not (toks |> Array.exists (fun t -> t = target + ":off" || t = "-" + target))

/// Does `BLADE_FP_CONTRACT` license contraction (fmul+fadd -> fma)?
///
/// THE SAME POLARITY AS THE g++ LANE (since 2026-10-17;
/// docs/plans/plan-fp-default-fastest.md). Unset means CONTRACT, the g++
/// lane's `-ffp-contract=fast`: the two Blade lanes now share one default
/// regime, and the owner's policy is that the default is the fastest form.
/// The old polarity (unset = off here, the "favorable inversion" of plan
/// section 5) existed to make DEFAULT LLVM IR byte-identical to the
/// interpreter for free; that is now an opt-in in both lanes, spelled the same
/// way: `BLADE_FP_CONTRACT=off` (plus BLADE_FP_REPRO=1 for the folds).
/// Measured cost of the old default (bench gap G19, blade-A F14): 0 `vfmadd`
/// in the gram/comoment3/antisym exes, 23 + 31 with the flag.
///
/// Spellings: unset / `fast` / `on` / `1` -> contract; `off` / `0` / any
/// other value -> no flag. `Build.llvmOptFlags` still passes
/// no `-ffp-contract` to clang: the flag would not reach IR that no C front
/// end produced; the per-instruction `contract` flag is the whole mechanism.
let private contractLicensed () : bool =
    if not (factEnabled factFmf) then false
    else
        match System.Environment.GetEnvironmentVariable "BLADE_FP_CONTRACT" with
        | null -> true
        | v ->
            match v.Trim().ToLowerInvariant() with
            | "" | "fast" | "on" | "1" -> true
            | _ -> false

// The TILE CORE (bench/notes/microkernel-sketches-review.md (removed 2026-10-24; in git history at cf2028cc) item M6): the
// packed-triangle SYRK tile of the C++ lane, reached from this lane through a
// C translation unit over the SAME arithmetic header (blade_tiles.h), not
// re-emitted as IR. Deployed beside the `.ll` by `deployShim`, compiled per
// policy key by `Build.buildLlvmTilesObject`, called by `materialize`.

/// The tile core's C driver (src/cpp/blade_llvm_tiles.c).
let tilesSourceFileName = "blade_llvm_tiles.c"
/// The arithmetic core both lanes include.
let tilesHeaderFileName = "blade_tiles.h"
/// The core's SYRK entry point (Float64, packed triangle, serial; M6).
let tilesSymbol = "blade_dsyrk_tri_f64"
/// The antisym pair's entry point (Float64, STRICT packed triangle; M7).
let antisymTileSymbol = "blade_dantisym_tri_f64"
/// The rank-3 simplex's entry point (Float64, packed SymIdx<3>; M8).
let simplexTileSymbol = "blade_dsym3_tri_f64"
/// Every entry point of the core: a `.ll` calling any of them links the
/// object (Build.compileLlvmProgram).
let tilesSymbols = [ tilesSymbol; antisymTileSymbol; simplexTileSymbol ]

/// The `-ffp-contract=` the tile object is compiled with: the C spelling of
/// THIS lane's per-instruction `contract` policy (`contractLicensed`), so the
/// tile contracts exactly where the IR it replaces would -- fused under the
/// default, never under BLADE_FP_CONTRACT=off or the fact kill switch.
let tilesContractFlag () : string =
    if contractLicensed () then " -ffp-contract=fast" else " -ffp-contract=off"

/// The scratch the core needs for a p x p triangle in blocks of kc samples,
/// in doubles: the B panel (kc x min(p, NC) padded to NR = 8) plus the A
/// scratch (g row panels per group, MR = 6 x kc each). blade_llvm_tiles.c
/// re-derives the same number and takes its scalar chain if handed less, so
/// a drift here costs speed, never memory safety.
let syrkTileScratchCells (p: int64) (kc: int64) (g: int64) : int64 =
    let nc = min p 2048L
    let npad = (nc + 7L) / 8L * 8L
    kc * npad + (max 1L (min 8L g)) * 6L * kc

/// The antisym core's column block (blade_llvm_tiles.c blade_llvm_anti_nc,
/// the C++ driver's anti_nc): ~8 MB of B panels, a multiple of the tile's
/// NR = `w`, clamped to [w, 4096].
let private antisymTileNc (n: int64) (w: int64) : int64 =
    let cap = (8L <<< 20) / 8L / 2L
    let nc = if n = 0L then 4096L else cap / n
    max w (min (nc / w * w) 4096L)

/// The scratch the antisym core needs, in doubles: the X(j) and Y(j) panels
/// over the whole sample range, n x min(NCb, p padded to NR) each, plus g
/// row panels' X(i) and Y(i) panels (the group, BLADE_TILE_TUNE anti_g /
/// `antisymRowGroup`), g x MR x n each. `paired` = the 4 x 4 schedule
/// (MR = NR = 4), else the sequential 6 x 8. The core re-derives the same
/// number and SILENTLY takes its scalar chain if handed less (same bits,
/// lost speed: `blade test llvm tiles` asserts the tile route).
let antisymTileScratchCells (p: int64) (n: int64) (paired: bool) (g: int64) : int64 =
    let rm, rn = if paired then 4L, 4L else 6L, 8L
    let ppad = (p + rn - 1L) / rn * rn
    let w = min (antisymTileNc n rn) ppad
    2L * n * w + 2L * (max 1L (min 8L g)) * rm * n

/// The scratch the simplex core needs, in doubles: every X(k) row packed once
/// into NR = 8-wide s-major panels (ceil(p / 8) * 8 * n) plus the W panel
/// (MR = 6 x kc). Re-derived by the core, as above.
let sym3TileScratchCells (p: int64) (n: int64) (kc: int64) : int64 =
    (p + 7L) / 8L * 8L * n + 6L * kc

// Attribute-group indices. Groups rather than inline attribute lists because
// every `define` in a Blade module carries the SAME set: one line at the
// bottom of the module beats the same twelve words on every function, and it
// is what clang itself emits.
//
// The split between 0 and 1 is the honest one. `willreturn`/`mustprogress`
// assert TERMINATION, which this lane can prove for a module whose loops are
// all statically counted (they all are -- runtime extents are refused) and
// whose call graph has no cycle. A single self-recursive function anywhere
// costs every function in the module those two attributes, because a
// non-recursive caller of a possibly-non-terminating callee does not itself
// terminate. `norecurse` stays per-function: mutual recursion is rejected by
// the front end (BL4006/BL2001 under define-before-use), so "does not call
// itself directly" IS "does not recurse".
let private grpFnTerminating = 0      // no recursion anywhere in the module
let private grpFnNoRecurse = 1        // this function does not self-call, but some function does
let private grpFnRecursive = 2        // this function self-calls
let private grpExternReturns = 3      // the shim's printers and libm: effects, but always returns
let private grpShimAlloc = 4          // the pool allocator
let private grpShimPanic = 5          // the panic path
let private grpShimFree = 6           // the pool deallocator (obviously not `nofree`)
let private grpShimArith = 7          // arithmetic-contract helpers: may panic (BL8013/BL8014), so no `willreturn`
let private grpTileCore = 8           // blade_llvm_tiles.c: pure, serial, argument memory only (M6)
let private grpShimArgRead = 9        // shim lookups that only READ argument memory (blade_compact1_rank)
let private grpShimRead = 10          // shim lookups that only READ memory, through pointers they load too (blade_member_has_i64)

let private attrGroupText (anyFrees: bool) (anyPanics: bool) (g: int) : string =
    // Blade has no exceptions (nounwind: deletes all Windows SEH lowering)
    // and no atomics or threads in this lane (nosync). `nofree` holds for the
    // whole module ONLY while no scope-exit free was emitted (`Ctx.AnyFrees`):
    // module-scope pools live to process exit as before, but a scoped body
    // that frees its temps makes every function's blanket claim false, so the
    // groups drop it together. (Groups are shared across functions by design;
    // per-function precision would need per-function attribute lists.)
    let drop (s: string) = if anyFrees then s.Replace("nofree ", "") else s
    // `willreturn` is a claim that the function RETURNS. A Blade function that
    // can reach a panic (a constraint check, a non-exhaustive match, an
    // arithmetic-contract helper) exits the process instead, and a `noreturn`
    // call inside a `willreturn` function is undefined behavior: LLVM deletes
    // the panic path and the program traps with no message (found by the
    // differential's abort-probe comparison). Like `nofree`, the claim drops
    // module-wide the moment any panicking shim symbol is declared.
    let dropWr (s: string) = if anyPanics then s.Replace(" willreturn", "") else s
    if g = grpFnTerminating then drop (dropWr "mustprogress nofree norecurse nosync nounwind willreturn")
    elif g = grpFnNoRecurse then drop "nofree norecurse nosync nounwind"
    elif g = grpFnRecursive then drop "nofree nosync nounwind"
    // The shim's printers do I/O and libm may set errno, so neither gets a
    // `memory(...)` claim -- only the two properties both really have. Their
    // `nofree` is a claim about the SYMBOL, not the module, and stays true.
    elif g = grpExternReturns then "nofree nounwind willreturn"
    // NOT `willreturn`: the allocator's out-of-memory arm calls blade_panic,
    // which exits the process instead of returning.
    elif g = grpShimAlloc then "nofree nounwind"
    elif g = grpShimPanic then "cold noreturn nounwind"
    elif g = grpShimFree then "nounwind willreturn"
    elif g = grpShimArith then "nofree nounwind"
    // The tile core's claims are about ITS SYMBOL (blade_llvm_tiles.c states
    // each one at the top of the file), so no module-wide drop applies: it
    // reads/writes only memory reachable from its pointer arguments (no
    // globals, no allocation, no I/O), has no threads or atomics, frees
    // nothing, and returns from every input (counted loops, no panic arm).
    elif g = grpTileCore then "mustprogress nofree nosync nounwind willreturn memory(argmem: readwrite)"
    // A pure lookup over its pointer argument (blade_compact1_rank's binary
    // search: the shim states the claims above its definition): reads only
    // argument memory, writes nothing, no globals, no I/O, always returns.
    elif g = grpShimArgRead then "mustprogress nofree nosync nounwind willreturn memory(argmem: read)"
    // The membership probe (blade_member_has_i64 over blade_member.h): it
    // reads the caller's structure AND the tables that structure points to
    // -- memory not BASED on its pointer argument in LLVM's sense, so the
    // claim is `memory(read)`, not `argmem: read`. Writes nothing, no I/O,
    // no threads, and returns on every input (a bounded scan, a bit test, or
    // a probe sequence that meets an empty slot at load <= 1/2).
    elif g = grpShimRead then "mustprogress nofree nosync nounwind willreturn memory(read)"
    else ""

/// The facts a whole module carries, computed once before any instruction is
/// built. Every field is a PROOF OBLIGATION discharged by a scan, not an
/// assumption: the comments name what would have to be true for the fact to be
/// wrong.
type private ModuleFacts =
    { /// Callable ids whose body calls themselves. Over-approximating this set
      /// is safe (it only withholds attributes); under-approximating it would
      /// assert `norecurse` about a recursive function.
      SelfRecursive: HashSet<IRId>
      /// True when `SelfRecursive` is non-empty -- see the group 0/1 note.
      AnyRecursion: bool
      /// True when NO element write exists anywhere in the module, which makes
      /// every array parameter `readonly`. The scan is whole-module rather
      /// than per-function because a write can be one call deep: a callee's
      /// `a(i) = v` lands in the pool ITS CALLER passed down, so a caller
      /// cannot be read-only unless nothing downstream writes either. Kernels
      /// are covered because they are callables in the same table.
      ArraysReadOnly: bool
      /// True when ANY assignment exists anywhere in the module -- an element
      /// write, a whole array or SCALAR reassignment (same scan as
      /// `ArraysReadOnly`; merged modules, functions, callable bodies, `mut`
      /// parameters and lowered let-rec writes included). Without one no
      /// value a deferred map reads can change after it is read, so rerunning
      /// it at its print yields its first-forcing cells -- half of the
      /// certificate under which a sole-read map may stay fused into its fold
      /// (gap G60 (b); soleReadModuleOperand).
      AnyAssignment: bool
      /// Ids whose `IRVar` occurs AT MOST ONCE across the whole module
      /// (bindings, function bodies, callable bodies). A sole-read deferred
      /// binding may be CONSUMED -- its producer run inside the one
      /// consumer -- instead of forced into a pool, because the slot-upgrade
      /// contract `materializeExpr` protects (later readers share storage)
      /// is vacuous with no later reader. The census over-approximates
      /// safely in one direction: an id NOT in the set forces, which is
      /// exactly today's behavior. Callee references and kernel slots are
      /// also spelled `IRVar`, so callable ids land in the counts too --
      /// harmless, since only array-operand reads ever consult this set.
      ReadOnce: HashSet<IRId>
      /// Array-returning functions whose return is a pool the CALLER owns --
      /// THIS LANE's own fixpoint, mirroring the C++ lane's
      /// `computeFreshReturnFacts` but WITHOUT its interior-view arm: the C++
      /// return arm copies a returned slice into a fresh pool, while this
      /// lane returns the view's GEP itself, so a view return is never
      /// caller-owned here (and the fixpoint must drop the arm TRANSITIVELY,
      /// or `f() = g()` over a view-returning `g` would still classify
      /// fresh). Absent means NotFresh: unproven is leaked, never freed --
      /// the same one-sided rule the C++ facts state.
      FreshReturns: HashSet<IRId> }

let private computeModuleFacts (m: IRModule) (callables: Map<IRId, IRCallable>) : ModuleFacts =
    // The callables table maps SEVERAL ids to one record: `let f = lambda(..)`
    // adds an alias key beside the callable's own id. Iterate DISTINCT
    // records, or every body behind an alias is scanned twice -- harmless for
    // the monotone facts below, fatal for the read census (a doubled count is
    // never sole-read).
    let distinctCallables =
        callables |> Map.toSeq |> Seq.map snd |> Seq.distinctBy (_.Id) |> Seq.toList
    // RECURSION IS A CYCLE, NOT A SELF-CALL. This used to ask only "does
    // cl's body call cl", which is blind to every cycle of length > 1 --
    // and those are reachable: a NESTED function sees the enclosing name
    // (TypeCheckInfer registers a function before checking its body), so
    //     function f(n) = { function h(m) = ... f(m - 1) ...; h(n) }
    // is a genuine f -> h -> f cycle that the front end accepts (only
    // SIBLING mutual recursion is rejected, BL4006/BL2001 under
    // define-before-use). Asserting `norecurse` there is a miscompile
    // license, not a missed optimization, and `willreturn`/`mustprogress`
    // would additionally claim a recursion terminates when nothing proved
    // it. Inlined kernels need no special case: the callable is in this
    // graph whether the emitter inlines its body or calls it.
    //
    // Edges first, then "can a node reach itself" by transitive closure.
    // Modules hold tens of callables, so the naive fixpoint is free.
    let calleesOf = Dictionary<IRId, HashSet<IRId>>()
    for cl in distinctCallables do
        let outs = HashSet<IRId>()
        iterIRExpr
            (fun n ->
                match n with
                // A call spells its callee as `IRVar` of a table id, which may
                // be an ALIAS entry whose id differs from the callable's own
                // -- so the edge lands on the RESOLVED callable's id.
                | IRApp (IRVar (fid, _), _, _) ->
                    (match Map.tryFind fid callables with
                     | Some target -> outs.Add target.Id |> ignore
                     | None -> ())
                | _ -> ())
            cl.Body
        calleesOf.[cl.Id] <- outs
    // Transitive closure of the edge relation, iterated to a fixpoint.
    let reach = Dictionary<IRId, HashSet<IRId>>()
    for KeyValue (id, outs) in calleesOf do reach.[id] <- HashSet<IRId>(outs)
    let mutable changed = true
    while changed do
        changed <- false
        for KeyValue (id, seen) in reach |> Seq.map (fun kv -> kv) |> Seq.toList do
            let additions = HashSet<IRId>()
            for t in seen do
                match reach.TryGetValue t with
                | true, more -> for x in more do if not (seen.Contains x) then additions.Add x |> ignore
                | _ -> ()
            if additions.Count > 0 then
                for x in additions do seen.Add x |> ignore
                changed <- true
    let selfRec = HashSet<IRId>()
    for KeyValue (id, seen) in reach do
        if seen.Contains id then selfRec.Add id |> ignore
    let mutable writes = false
    let mutable anyAssign = false
    let scanWrites (e: IRExpr) =
        iterIRExpr
            (fun n ->
                (match n with IRAssign _ -> anyAssign <- true | _ -> ())
                match n with
                // An element write through a (possibly `mut`) array.
                | IRAssign (IRIndex _, _) -> writes <- true
                // A whole-variable assignment rebinds a slot; it stores into an
                // alloca or re-points an array binding, and touches no pool.
                // (It still changes what a later read of that name sees:
                // AnyAssignment.)
                | IRAssign (IRVar _, _) -> ()
                // Any other assignment target is a shape this emitter has no
                // arm for. It will refuse later; until then, assume it writes.
                | IRAssign _ -> writes <- true
                | _ -> ())
            e
    for b in m.Bindings do scanWrites b.Value
    for f in m.Functions do scanWrites f.Body
    for cl in distinctCallables do scanWrites cl.Body
    // The read census for `ReadOnce`: every `IRVar` occurrence, everywhere
    // the writes scan looks. One traversal set, deliberately shared, so a
    // reader of either fact audits the same coverage.
    let reads = Dictionary<IRId, int>()
    let countReads (e: IRExpr) =
        iterIRExpr
            (fun n ->
                match n with
                | IRVar (vid, _) ->
                    reads.[vid] <- (match reads.TryGetValue vid with | true, k -> k | _ -> 0) + 1
                | _ -> ())
            e
    // The census roots are the EMISSION roots -- module bindings plus the
    // callables table -- and deliberately NOT `m.Functions`: the table IS
    // built from `m.Functions` (IRPrint.buildCallablesTable maps each one to
    // an entry), so listing both scans every function body twice and nothing
    // is ever sole-read. Distinct-by-record above closes the same hole for
    // alias keys.
    for b in m.Bindings do countReads b.Value
    for cl in distinctCallables do countReads cl.Body
    let readOnce = HashSet<IRId>()
    for KeyValue (vid, k) in reads do
        if k <= 1 then readOnce.Add vid |> ignore
    // The fresh-return fixpoint (see the field's note). The two positive arms
    // are exactly the shapes whose emission provably allocates INSIDE the
    // body: a lifted materializer tail, or a sole-use binding of one; a
    // returned call chains through the fact.
    let freshReturns =
        let isLifted (e: IRExpr) =
            match e with
            | IRCompute (IRApplyCombinator _) | IRArrayLit _ -> true
            | _ -> false
        let unrolled =
            m.Functions
            |> List.filter (fun f -> match f.RetType with ArrayElem _ -> true | _ -> false)
            |> List.map (fun f -> (f.Id, Blade.CodeGenLoopNest.deepUnrollBody f.Body))
        let acc = HashSet<IRId>()
        let rec fix () =
            let mutable changed = false
            for (fid, (lets, retExpr)) in unrolled do
                if not (acc.Contains fid) then
                    let fresh =
                        if isLifted retExpr then true
                        else
                            match retExpr with
                            | IRVar (rid, _) ->
                                let boundToLifted =
                                    lets |> List.exists (fun (id, v) -> id = rid && isLifted v)
                                let otherUses =
                                    lets |> List.exists (fun (id, v) ->
                                        id <> rid && Set.contains rid (collectVarRefsIR v))
                                boundToLifted && not otherUses
                            | IRApp (f, _, _) ->
                                match resolveCallable f with
                                | Some cl -> acc.Contains cl.Id
                                | None -> false
                            | _ -> false
                    if fresh then
                        acc.Add fid |> ignore
                        changed <- true
            if changed then fix ()
        fix ()
        acc
    { SelfRecursive = selfRec
      AnyRecursion = selfRec.Count > 0
      ArraysReadOnly = not writes
      AnyAssignment = anyAssign
      ReadOnce = readOnce
      FreshReturns = freshReturns }

/// MODULE READS -- which top-level bindings each callable can read, through
/// everything it calls or inlines. An out-of-line function gets fresh slot
/// tables (emitFunctionBody), so a module binding it reads must cross the
/// function boundary some other way: `main` PUBLISHES it to an internal
/// global, and the function reads the global. The closure runs over every
/// callable reference -- calls, kernel slots, inlined capturing callees --
/// because a kernel inlined INTO a function body resolves through that
/// function's tables, not main's (an over-approximation only publishes a
/// binding nobody reads across a call). A loop object or callable alias has no
/// storage of its own, so a read of one is a read of what its value names.
/// Returns (callable id -> transitive module reads, bindings to publish): the
/// second set is the union over CALL ROOTS -- callables with no captures that
/// appear in call position, the ones emitCall outlines.
///
/// A DEFERRED MODULE MAP (an un-forced map binding, or a top-level alias of
/// one -- the third result, id -> declaration index) is both: a read of it is
/// recorded, AND -- because a function body evaluates it privately from its
/// value (gap G75) -- an edge to everything that value reads, so its inputs
/// are published for the function.
let private computeModuleReads (m: IRModule) (callables: Map<IRId, IRCallable>)
    : Dictionary<IRId, HashSet<IRId>> * HashSet<IRId> * Dictionary<IRId, int> =
    let isOpaqueValue (v: IRExpr) =
        match v with
        | IRMethodFor _ | IRObjectFor _ | IRComposeObj _ | IRCompose _ -> true
        | _ -> false
    let storageIds = HashSet<IRId>()
    let opaque = Dictionary<IRId, IRExpr>()
    let deferredMaps = Dictionary<IRId, int>()
    m.Bindings |> List.iteri (fun i b ->
        if not (callables.ContainsKey b.Id) then
            if isOpaqueValue b.Value then opaque.[b.Id] <- b.Value
            else
                storageIds.Add b.Id |> ignore
                let isArray = match Blade.IR.stripUnits b.Type with ArrayElem _ -> true | _ -> false
                let deferredForm =
                    match b.Value with
                    | IRApplyCombinator _ | IRComposeApply _ | IRFunctorMap _ -> true
                    | IRVar (src, _) -> deferredMaps.ContainsKey src
                    | _ -> false
                if isArray && deferredForm then deferredMaps.[b.Id] <- i)
    // Nodes are callables (by their OWN id, aliases resolved) and opaque
    // module bindings; edges are references, reads land on storage ids.
    let direct = Dictionary<IRId, HashSet<IRId>>()
    let edges = Dictionary<IRId, HashSet<IRId>>()
    let roots = HashSet<IRId>()
    let scan (node: IRId) (e: IRExpr) =
        let reads = HashSet<IRId>()
        let outs = HashSet<IRId>()
        iterIRExpr
            (fun n ->
                match n with
                | IRVar (id, _) ->
                    (match Map.tryFind id callables with
                     | Some t -> outs.Add t.Id |> ignore
                     | None ->
                         if storageIds.Contains id then
                             reads.Add id |> ignore
                             if deferredMaps.ContainsKey id then outs.Add id |> ignore
                         elif opaque.ContainsKey id then outs.Add id |> ignore)
                | _ -> ())
            e
        direct.[node] <- reads
        edges.[node] <- outs
    let distinctCallables =
        callables |> Map.toSeq |> Seq.map snd |> Seq.distinctBy (_.Id) |> Seq.toList
    for cl in distinctCallables do scan cl.Id cl.Body
    for KeyValue (id, v) in opaque do scan id v
    for b in m.Bindings do
        if deferredMaps.ContainsKey b.Id then scan b.Id b.Value
    let noteRoots (e: IRExpr) =
        iterIRExpr
            (fun n ->
                match n with
                | IRApp (IRVar (fid, _), _, _) ->
                    (match Map.tryFind fid callables with
                     | Some t when List.isEmpty t.Captures -> roots.Add t.Id |> ignore
                     | _ -> ())
                | _ -> ())
            e
    for b in m.Bindings do noteRoots b.Value
    for cl in distinctCallables do noteRoots cl.Body
    // reads*(n) = direct(n) U reads*(every node n references), to a fixpoint.
    let closure = Dictionary<IRId, HashSet<IRId>>()
    for KeyValue (id, r) in direct do closure.[id] <- HashSet<IRId>(r)
    let mutable changed = true
    while changed do
        changed <- false
        for KeyValue (id, outs) in edges do
            let mine = closure.[id]
            for t in outs do
                match closure.TryGetValue t with
                | true, theirs ->
                    for x in theirs do
                        if mine.Add x then changed <- true
                | _ -> ()
    let publish = HashSet<IRId>()
    for r in roots do
        match closure.TryGetValue r with
        | true, reads -> publish.UnionWith reads
        | _ -> ()
    (closure, publish, deferredMaps)

// ---------------------------------------------------------------------------
// Typed builders
//
// Every instruction shape with more than two slots is a named-field record
// (the EmitCpp.fs discipline): a positional sprintf with five holes is how an
// operand and a type end up transposed in a way no reader catches. Each
// builder renders exactly one `.ll` line.
// ---------------------------------------------------------------------------

/// An emitted value: the textual operand (an SSA register, or a literal --
/// `.ll` takes constants wherever it takes registers) plus its scalar type.
type private Val = { Reg: string; Ty: Sc }

/// A match scrutinee with its tuple NESTING restored. Tuples travel as flat
/// packs in this lane (Tuples Design C), but a pattern like `((a, b), c)` or
/// `h :: rest` is structural, so emitMatch rebuilds the tree from the
/// scrutinee's static type before testing patterns against it.
type private MatchTree =
    | MLeaf of Val
    | MNode of MatchTree list

type private BinInstr =
    { Dest: string
      Opcode: string
      /// Fast-math flags, ALREADY carrying a leading space when non-empty
      /// (`" reassoc nsz"`). A required field rather than an optional one on
      /// purpose: every arithmetic site in this file then states, in its own
      /// text, whether it is licensed to reassociate -- which is the whole
      /// discipline the numeric policy rests on. Integer sites pass "".
      Flags: string
      Ty: Sc
      Lhs: string
      Rhs: string }

type private CmpInstr =
    { Dest: string; Kind: string    // "icmp" | "fcmp"
      Pred: string; Ty: Sc; Lhs: string; Rhs: string }

type private ConvInstr =
    { Dest: string; Opcode: string; From: Sc; Value: string; To: Sc }

type private CallInstr =
    { Dest: string option; RetTy: Sc; Callee: string; Args: (Sc * string) list }

type private BrInstr =
    { Cond: string; IfTrue: string; IfFalse: string }

let private renderBin (i: BinInstr) =
    $"{i.Dest} = {i.Opcode}{i.Flags} {(llTy i.Ty)} {i.Lhs}, {i.Rhs}"

let private renderCmp (i: CmpInstr) =
    $"{i.Dest} = {i.Kind} {i.Pred} {(llTy i.Ty)} {i.Lhs}, {i.Rhs}"

let private renderConv (i: ConvInstr) =
    $"{i.Dest} = {i.Opcode} {(llTy i.From)} {i.Value} to {(llTy i.To)}"

let private renderCall (i: CallInstr) =
    let args = i.Args |> List.map (fun (t, v) -> $"{(llTy t)} {v}") |> String.concat ", "
    match i.Dest with
    | Some d -> $"{d} = call {(llTy i.RetTy)} {i.Callee}({args})"
    | None -> $"call {(llTy i.RetTy)} {i.Callee}({args})"

let private renderBr (i: BrInstr) =
    sprintf "br i1 %s, label %%%s, label %%%s" i.Cond i.IfTrue i.IfFalse

// ---------------------------------------------------------------------------
// Array shapes: dense axes and packed simplex groups
//
// An array's shape is a list of GROUPS, not a list of extents, because that is
// the granularity storage is decided at. A plain `Idx<n>` axis is its own
// group and stores n cells; a `SymIdx<r,n>` / `AntisymIdx<r,n>` group spans r
// AXES but stores only the canonical simplex -- C(n+r-1, r) or C(n, r) cells.
//
// TWO COORDINATE SYSTEMS, and every function below says which it takes.
//   * CANONICAL coordinates are the absolute ones: i <= j (or i < j).
//   * STORAGE coordinates are left-justified into each shrinking row:
//     (i, p) with p in [0, n - i - strict), canonical j = i + p + strict.
// The emitter works in STORAGE coordinates everywhere except the one place a
// user writes an index (`IRIndex`), which converts. That is not a taste
// decision: it is the convention the C++ lane already emits and the corpus
// already pins (loops/170-175 -- "the kernel receives PACKED STORAGE
// COORDINATES, the raw triangular loop counters"), so agreeing with it is how
// the two lanes' kernels see the same numbers.
// ---------------------------------------------------------------------------

/// One axis group of an array's shape. v1 admits compact groups of RANK 2
/// only; a rank-3+ simplex needs the multi-level left-justification the
/// storage plan calls prisms, and refuses by name at the construction sites.
type private Grp =
    | GDense of int64
    | GSym of int * int64
    | GAnti of int * int64
    /// A RAGGED PAIR: ONE group spanning TWO axes -- the static outer row
    /// axis and the per-row inner axis -- whose row base is a TABLE LOOKUP
    /// (offsets[i]) instead of a closed form. The static/dynamic split the
    /// IR already makes (IxKRaggedInline literals bake their lens; a ragged
    /// function parameter's lens arrive at run time) lives in the TABLE, so
    /// addressing and loop shapes are one code path for both lanes.
    | GRagged of rows: string * table: RaggedTable
    /// RANK 1 with an OPERAND extent: the residual of peeling one row off a
    /// ragged group. The operand is a literal for a static-lane row peeled at
    /// a literal index, a register otherwise -- either way `emitCountedLoopTo`
    /// takes it as-is.
    | GDynDense of len: string
    /// A RANK-1 COMPOUND (`compound(dense, mask)` over ONE dense axis, no
    /// trailing dims -- bench gap G9): the compaction representation. Storage
    /// is `len` cells in kept order, exactly a GDynDense for every flat
    /// consumer (fold, print, map, extents); `pos` is the kept-position table
    /// (an i64 pool of `len` ascending source positions, the same table
    /// compound_index_t<1>::rank_to_tuple holds in the C++ lane), which a
    /// KEYED read `S(k)` binary-searches -- a key that was not kept is BL8006.
    /// A map over a compound keeps the same `pos` (identical mask by
    /// construction), as the C++ lane's shared-index output does.
    | GCompact of len: string * pos: string
    /// A CLOSED-FORM ENUMERABLE DOMAIN (a static struct with linear
    /// conjuncts): ONE group spanning the struct's fields, coordinates =
    /// field values, cells = the plan's solutions in lex order (see
    /// `emitDomainNest`).
    | GDomain of plan: DomainPlan

/// Where a ragged group's offsets live. rows+1 entries; offsets[rows] is the
/// total cell count. The STATIC lane carries the values (folded straight into
/// addressing at literal indices) plus a `private unnamed_addr constant`
/// global for loop-variable indices -- `constant` proves no store can touch
/// it, which lets consecutive rows share one load. The DYNAMIC lane holds a
/// runtime `ptr` (a ragged parameter's sidecar argument) with IDENTICAL
/// addressing -- the two lanes differ in one operand token.
and private RaggedTable =
    | RtStatic of offsets: int64[] * tableSym: string
    | RtDynamic of tablePtr: string

/// A module binding `main` published for out-of-line functions (see
/// computeModuleReads): the internal global's symbol plus, for an array, the
/// shape a function rebuilds its view from -- only the pool pointer crosses,
/// and only static shapes are published.
type private PubVal =
    | PubScalar of sym: string * ty: Sc
    | PubArray of sym: string * elem: Sc * groups: Grp list

let private grpRank = function GDense _ | GDynDense _ | GCompact _ -> 1 | GSym (r, _) -> r | GAnti (r, _) -> r | GRagged _ -> 2 | GDomain p -> p.Levels.Length
let private grpExtent =
    function
    | GDense n -> n | GSym (_, n) -> n | GAnti (_, n) -> n
    | GRagged _ -> refuse "a ragged index group where a single static extent is required"
    | GDomain _ -> refuse "an enumerated-domain group where a single static extent is required"
    | GDynDense _ -> refuse "a runtime-extent axis where a static extent is required"
    | GCompact _ -> refuse "a compound (runtime-cardinality) axis where a static extent is required"
/// Antisymmetric groups are STRICT (i < j) and store no diagonal.
let private grpStrict (g: Grp) = g.IsGAnti
let private grpCompact = function GSym _ | GAnti _ -> true | _ -> false
let private isRaggedGrp (g: Grp) = g.IsGRagged
let private hasRagged (gs: Grp list) = gs |> List.exists isRaggedGrp

/// A rank-1 dense group from an extent OPERAND: the literal spelling keeps
/// the static `GDense` (so every existing consumer stays on its static path),
/// a register becomes the runtime-extent axis.
let private denseOfOp (ext: string) : Grp =
    match System.Int64.TryParse ext with
    | true, n -> GDense n
    | _ -> GDynDense ext

/// How many AXES a group spans, as extents (a rank-2 group spans two).
let private grpAxes (g: Grp) : int64 list = List.replicate (grpRank g) (grpExtent g)

let private axisExtents (gs: Grp list) : int64 list = gs |> List.collect grpAxes

/// Stored cells in one group -- the cardinality the pool is allocated at.
let private grpCells (g: Grp) : int64 =
    match g with
    | GDense n -> n
    | GSym (r, n) -> Blade.SimplexBlocksCore.binom (n + int64 r - 1L) r
    | GAnti (r, n) -> Blade.SimplexBlocksCore.binom n r
    // offsets[rows] IS the total cell count -- the static lane's one number.
    | GRagged (rows, RtStatic (offsets, _)) ->
        (match System.Int64.TryParse rows with
         | true, r -> offsets.[int r]
         | _ -> refuse "a ragged shape with a runtime row count where a static cell count is required")
    | GRagged (_, RtDynamic _) -> refuse "a dynamic ragged shape where a static cell count is required"
    | GDomain p -> p.Card
    | GDynDense _ -> refuse "a runtime-extent axis where a static cell count is required"
    | GCompact _ -> refuse "a compound (runtime-cardinality) axis where a static cell count is required"

let private shapeCells (gs: Grp list) : int64 = gs |> List.fold (fun acc g -> acc * grpCells g) 1L

let private isDenseShape (gs: Grp list) : bool = gs |> List.forall (grpCompact >> not)

let private denseGroups (extents: int64 list) : Grp list = extents |> List.map GDense

/// The shape that is EXACTLY one compact simplex, at any rank: `(r, n,
/// strict)`. Whole-array schedules (the flat pool walk, the blocked
/// decomposition) are defined on this shape and no other, so the predicate
/// lives in one place.
let private soleSimplexR (gs: Grp list) : (int * int64 * bool) option =
    match gs with
    | [ GSym (r, n) ] -> Some (r, n, false)
    | [ GAnti (r, n) ] -> Some (r, n, true)
    | _ -> None

// ---------------------------------------------------------------------------
// Emitter state
// ---------------------------------------------------------------------------

/// A STATIC-EXTENT array value: a single flat pool (no Iliffe skeleton),
/// with every extent a compile-time constant baked into the GEPs and bounds.
/// The pool is row-major over the shape's groups, and a compact group
/// contributes its canonical simplex in ascending-lex order -- byte-for-byte
/// the order `linearized_storage`'s `linearize` and the C++ lane's pointer
/// skeleton put cells in.
///
/// `Src` is the one interesting field. A materialized array is a `ptr`; a
/// DEFERRED one (`method_for(...) <@> f`, `range<I>`, `A + B` before
/// `|> compute`) is a PRODUCER -- a function from index registers to the
/// element's value -- and is never given storage at all. That is the same
/// laziness the C++ lane spells with `DeferredComputations`, but expressed
/// as an F# closure instead of an IR map, so `|> compute` is literally
/// "run the producer into a pool" and `reduce` over an unforced computation
/// is "run the producer inside the fold" with no array in between.
type private ArrVal =
    { Elem: Sc
      /// Outermost group first.
      Groups: Grp list
      Src: ArrSrc
      /// The REUSE HINT (plan-simplex-blocked-compute.md section 0, third
      /// block): bytes of the largest operand a kernel parameter binds as a
      /// ROW VIEW, 0 when every bind is a scalar. Computed once at
      /// construction by the producer that knows (applyToArr); everything
      /// else says 0. Consulted only by brickTileEdge's BrickAuto branch --
      /// a plain map's cells read scalars, so maps carry 0 and never brick
      /// by default, which is the second-block verdict.
      RowOpBytes: int64 }
    /// Per-AXIS extents, outermost first: a rank-2 compact group contributes
    /// its extent twice. Length = the array's rank, which is what every
    /// consumer that counts index slots means by rank.
    member this.Extents = axisExtents this.Groups
    /// The number of index SLOTS, computed without touching extents -- safe
    /// on ragged shapes, whose extent list has no static answer.
    member this.Rank = this.Groups |> List.sumBy grpRank

and private ArrSrc =
    | APool of string
    /// STORAGE coordinates (one i64 Val per axis) -> element value.
    | AVirt of (Ctx -> Val list -> Val)
    /// FLAT CELL ORDINAL -> element value: the cell-congruent producer.
    ///
    /// This is the flat-elementwise path the blocked-simplex plan tells us not
    /// to touch (section 6: "elementwise maps over sym arrays -- flat-pool
    /// traversal, already optimal, ZERO coordinate math; do not touch this
    /// path"). An elementwise map whose operands all carry the SAME shape as
    /// its output needs no coordinates at all: cell k of the result is the
    /// kernel of cell k of each operand, whatever the shape means. Keeping it
    /// a separate producer flavour is what makes `S * 3.0` over a packed
    /// triangle ONE counted loop over the pool rather than a triangular nest
    /// with row-base arithmetic per cell.
    | AVirtFlat of (Ctx -> Val -> Val)

/// What a Blade value expression produced. Not the same axis as `IRType`: a
/// fused reduction join is one loop with k accumulators and answers a FLAT
/// PACK of registers, and an unforced computation answers a producer -- both
/// decided by the expression, not by the type the checker stamped on it.
and private ValKind =
    | VScalar of Val
    | VArray of ArrVal
    /// A flat PACK: `(L <@> f) <&> (L <@> g)` is two arrays, a reduction join
    /// is k accumulators, a destructured tuple is its components. Flat because
    /// `IRTupleProj` indexes flat (docs: Tuples Design C).
    | VTuple of ValKind list
    /// A binding that names something with no runtime representation at all:
    /// a loop object (`method_for(...)`, `object_for(...)`) or an alias for a
    /// lifted callable (`let f = lambda(x) -> ...`). No storage, no code, no
    /// printed line -- and the C++ lane emits a comment for exactly these.
    | VOpaque

/// One argument handed to an inlined kernel. A kernel parameter is a scalar
/// element at a fully-peeled level and a ROW VIEW where the nest stopped
/// short of the array's rank (`reduce(row, ...)` kernels), so the two shapes
/// travel together.
and private KArg =
    | KScalar of Val
    | KArray of ArrVal

/// One open pool-tracking scope. `At` is the `c.Body` index where the scope
/// opened: the scope-entry null resets are INSERTED there at pop time, so
/// every EXECUTION of the scope starts its tracking slots at null -- an
/// alloca does not zero, and a pool allocated on only one branch of the body
/// must not leave last execution's (already freed) pointer for the exit
/// frees. `Pools` pairs each pool register with its tracking slot; the frees
/// load the slot rather than naming the register, so a conditionally
/// allocated pool needs no dominance argument. `KnownIds` snapshots which
/// array bindings existed when the scope opened: a pool that later UPGRADES
/// such a binding (`materializeExpr`, whole-array rebinding) outlives the
/// scope and is kept by nulling its slot at the escape site.
and private PoolScope =
    { At: int
      Pools: ResizeArray<string * string>
      mutable InlineId: IRId option
      KnownIds: HashSet<IRId> }

/// One emitted group_keys binding: the llvm-lane port of the C++ lane's
/// name-suffixed CSR ABI (`<name>__ngroups/__offsets/__perm/__nsrc`,
/// CodeGenBinding.fs) as four values keyed by the binding's IRId -- BL3017
/// guarantees a grouping is only ever its bare binding name, so a table keyed
/// by id IS the whole aliasing story. NGroups is an OPERAND: a literal for
/// the annotated regimes (Idx<N> positional, int EnumIdx), a register for
/// dynamic first-occurrence discovery. NSrc stays compile-time (the key
/// array's own static extent), and is carried because a dropped (negative)
/// key leaves __perm under-filled: offsets[ngroups] cannot recover it.
and private GkVal =
    { NGroups: string
      Offsets: string
      Perm: string
      NSrc: int64
      /// AGGREGATE-ONLY GROUPING (Optimize.planGroupAggregate): the one
      /// pass's per-group row counts and one pool per peel state ("" for a
      /// count state); None for the ordinary CSR (whose Perm is real).
      Agg: (string * string list) option }

and private Ctx =
    { /// Module-scope constants (string literals for print labels and panics).
      Globals: ResizeArray<string>
      /// Open pool-tracking scopes, innermost last (a function body, or one
      /// IRForRange trip). While any scope is open, `allocPool` records each
      /// pool into the innermost one, and the scope's exit frees what was
      /// recorded -- the arena model's repair for allocations in REPEATED
      /// contexts (section 0.7/0.8 of the plan). Empty at module scope, where
      /// a pool is allocated once per program and the arena is already sound.
      PoolScopes: ResizeArray<PoolScope>
      /// True once any `blade_free` was emitted: the module-tail attribute
      /// groups drop their `nofree` claim, which stops being true the moment
      /// one scope frees.
      mutable AnyFrees: bool
      StringPool: Dictionary<string, string>
      /// Per-coordinate offset tables of the canonicalizing compact read
      /// (`coordTableGlobal`), by "strict:n:r:k" -> module constant.
      CoordTables: Dictionary<string, string>
      /// `declare` lines, deduplicated and sorted so emission is deterministic.
      Externs: SortedSet<string>
      /// Finished function definitions, in emission order.
      Funcs: ResizeArray<string list>
      /// Every callable the module offers, including let-alias entries.
      Callables: Map<IRId, IRCallable>
      /// Callable id -> LLVM symbol, populated at FIRST REFERENCE so a
      /// self-recursive body can call the symbol it is still inside of.
      Emitted: Dictionary<IRId, string>
      Pending: Queue<IRCallable>
      /// Registers and labels are numbered from one process-wide counter, so
      /// no two labels can collide even across function boundaries.
      mutable Counter: int
      /// Per-function: IR variable id -> (alloca register, type).
      mutable Slots: Dictionary<IRId, string * Sc>
      /// Per-function: IR variable id -> array value. Separate from `Slots`
      /// because an array binding has no single scalar register: it is a
      /// pointer plus a shape, or a closure.
      mutable ArrSlots: Dictionary<IRId, ArrVal>
      /// Per-function: parameter NAME -> slot, for the `IRParam` spelling of a
      /// parameter reference (function bodies use `IRVar` of the param's
      /// VarId, kernel-shaped bodies use `IRParam`; both reach the same slot).
      mutable NameSlots: Dictionary<string, string * Sc>
      mutable NameArrSlots: Dictionary<string, ArrVal>
      /// Loop-object bindings (`let L = method_for(A, B)`). They hold no
      /// storage and print nothing; the table exists so a reference to one
      /// can be diagnosed as a loop object instead of an unbound variable.
      LoopObjs: HashSet<IRId>
      /// Emitted group_keys bindings, by binding id (see GkVal).
      GroupKeys: Dictionary<IRId, GkVal>
      /// Bindings whose value is a flat tuple of scalars (fusion-join folds,
      /// tuple-returning kernels). `IRTupleProj` reads these.
      mutable TupleSlots: Dictionary<IRId, ValKind list>
      /// Deferred bindings a consumer MATERIALIZED under their own name.
      /// Mirrors `CodeGen.forcedDeferredIdsCell`: such a binding is a real
      /// array by program end and therefore prints, while one that stayed
      /// deferred through the whole program prints nothing.
      Forced: HashSet<IRId>
      /// Top-level binding id -> its value expression. Read only to propagate
      /// forcing: materializing a deferred binding materializes the deferred
      /// bindings ITS value names too (the C++ lane's `forceDeferredArrayInput`
      /// recursion), and each of those then prints.
      mutable BindingValues: Map<IRId, IRExpr>
      /// A LOCAL array let bound to a still-deferred producer -> that producer's IR,
      /// so applyToArr can audit it before keeping its rows streaming.
      DeferredLetValues: Dictionary<IRId, IRExpr>
      /// Each virtual domain map's kernel body, keyed by its AVirt producer
      /// closure (by reference): what a fold over it hands domainFoldOrder, the
      /// order decision the C++ lane and `blade plan` share.
      DomainMapBodies: Dictionary<obj, IRExpr>
      /// Callables currently being inlined (capturing callees), so a
      /// recursive one refuses instead of expanding forever.
      Inlining: HashSet<IRId>
      /// Callable id -> the module bindings it can read, transitively
      /// (computeModuleReads). Consulted at a call site -- everything the
      /// callee reads must already be published -- and at a function's entry,
      /// where those bindings are rebound to their globals.
      mutable ModuleReads: Dictionary<IRId, HashSet<IRId>>
      /// The bindings `main` publishes as it binds them.
      mutable PublishWanted: HashSet<IRId>
      /// What has been published so far, by binding id.
      Published: Dictionary<IRId, PubVal>
      /// Top-level binding id -> its source name, for refusal messages.
      mutable ModuleNames: Map<IRId, string>
      /// DEFERRED MODULE MAPS (computeModuleReads' `deferredMapIds`): binding
      /// id -> declaration index. A function body that reads one evaluates it
      /// PRIVATELY, inside each call, from the module bindings its value reads
      /// (gap G75, formalism 10.2: such a binding is never stored under its
      /// own name and never prints); main's calls admit that only while the
      /// binding is still unforced there.
      mutable DeferredModule: Dictionary<IRId, int>
      /// True while emitting an out-of-line function body (false in main).
      mutable InFunction: bool
      /// `c.Body` index where the current TOP-LEVEL statement of main began,
      /// or -1 anywhere else. Forcing a deferred module binding is hoisted to
      /// this point (see materializeExpr).
      mutable StmtStart: int
      /// main's array bindings as they stood when the current top-level
      /// statement began. A forced binding is hoisted only if it is still
      /// that same instance: one rebound earlier in this statement holds
      /// registers the hoisted code would precede.
      mutable StmtSlots: Dictionary<IRId, ArrVal>
      /// Bindings whose force was hoisted in the current statement: their
      /// upgraded slots live in the hoisted prefix, so a later hoist may read
      /// them.
      HoistedIds: HashSet<IRId>
      /// The SPINE of the current statement, by reference: main's top-level
      /// statement value, or a function's body, then every `let` of a chain,
      /// each let's value, every element of a sequence and the tail --
      /// straight-line code in its scope. In a FUNCTION BODY, entering a
      /// spine node starts a POSITIONAL statement there (spineStatement): a
      /// module force met inside it is hoisted to the node's start, after
      /// every effect emitted before it. In MAIN a node only marks itself
      /// (SpineNode): ordinary forces keep the base boundary, the start of
      /// the whole statement (the C++ lane's pre-pass), and only the
      /// block-alias site (bindNamed), a value tuple's members (gaps G58 /
      /// G61, emitTupleParts) and the root call's ordered arguments (gap
      /// G57, emitCall; SpineCallArgs) take node-local points (setSpine).
      mutable Spine: HashSet<obj>
      /// The argument lists of the spine's calls, by reference.
      mutable SpineCallArgs: HashSet<obj>
      /// The spine node whose entry started the current positional statement
      /// (null when none): a block alias forces its source with a hoist only
      /// when that hoist lands at the alias itself (bindNamed, gap G60 (c)).
      mutable SpineNode: obj
      /// The spine's nodes start positional statements (a function body's
      /// spine) or only mark themselves (main's; see setSpine).
      mutable SpinePositional: bool
      /// MAIN: the ids the current top-level statement reads POSITIONALLY
      /// anywhere (an IRIndex / IRExtent / IRContains base, a call argument,
      /// an assignment RHS) -- the set the C++ lane's whole-statement
      /// pre-pass (collectDeferredPositionalReads) forces at the statement's
      /// START. A block alias of one of them copies the stored source; it
      /// forces at the alias only when the pre-pass does not.
      mutable StmtPositionalReads: HashSet<IRId>
      /// MAIN: the same set in the C++ walk's first-read order (as IRVar
      /// nodes). A BLOCK statement forces these up front, before any block
      /// effect or alias upgrade (emitMain; review codex-diff-W4B-B-4).
      mutable StmtForceOrder: IRExpr list
      /// MAIN: the tuple nodes whose members may take their own force points
      /// -- the statement's root value and the tail a let chain ends in (the
      /// C++ lane member-orders a binding's tuple value there), never an
      /// intermediate let's initializer, which the pre-pass covers.
      mutable TupleRoots: HashSet<obj>
      /// MAIN: the tuple a top-level statement's let chain ends in (null when
      /// none). The C++ lane leaves such a tuple's reads to the final binding
      /// (no whole-chain pre-pass), so its forces start at the tuple, after
      /// the chain's lets (tuples/043).
      mutable TupleTail: obj
      /// Open IRForRange loops, innermost on top: the exit label an IRBreakIf
      /// branches to (minted by the FIRST break, "" until then), and the
      /// PoolScopes depth BEFORE the loop's trip scope (the scopes a break
      /// must free).
      BreakTargets: Stack<string ref * int>
      /// ENTRY-BLOCK allocas, spliced ahead of the body at assembly time. An
      /// `alloca` inside a loop body would allocate once PER ITERATION (stack
      /// is reclaimed at function return, not at block exit), so a nest over
      /// a real extent would run the stack out; hoisting also keeps every
      /// slot in the entry block, which is where mem2reg promotes them.
      mutable Allocas: ResizeArray<string>
      mutable Body: ResizeArray<string>
      /// What the front end proved about this module, computed before the
      /// first instruction (see the fact-layer note above).
      Facts: ModuleFacts
      /// Open ONLY while emitting a licensed fold's combine. Float arithmetic
      /// born inside this scope carries `reassoc nsz`; everything else stays
      /// order-preserving. Scoped rather than global because the same kernel
      /// text appears in map position too, where no license exists.
      mutable FoldFmf: bool
      /// Set while emitting a fold whose deferred OPERAND writes a captured
      /// binding (gap G41): its accumulator keeps the serial association --
      /// no fold FMF -- exactly as the C++ lane's `licensedFold` declines.
      mutable FoldOperandWrites: bool
      /// The span of the declaration being emitted -- the top-level binding
      /// main is binding, or the function whose body is open. A runtime GUARD
      /// panic with no expression span of its own (non-exhaustive match,
      /// group-key range, empty reduce) is located here, exactly as the C++
      /// lane's `CodeGenState.declPanicArgs` locates its twin, so the two
      /// lanes print the same `  --> file:line`.
      mutable DeclSpan: Blade.Ast.Span
      /// The NAME of that declaration (the binding or function), the subject
      /// of this lane's own `blade plan` records (the syrk tile, M6).
      mutable DeclName: string
      /// Attribute groups actually referenced, so the module's tail lists the
      /// ones it uses and no others.
      UsedAttrGroups: SortedSet<int>
      /// MEMBERSHIP HOISTING (Optimize.planMembershipHoist): set binding id
      /// -> the entry-block slot of the search structure `emitMain` built
      /// for the host statement being emitted. Live ONLY while that host's
      /// statement is emitted in main (removed at its free), so a probe
      /// anywhere else -- another statement, an out-of-line function --
      /// takes the linear scan.
      MemberTables: Dictionary<IRId, string> }

/// A deferred value the TILE CORE may materialize (M6): a same-array `comm`
/// row contraction `method_for(X, X) <@> lambda(a, b) where comm(a, b) ->
/// prodsum(a, b) [/ d | * s]`, matched by the C++ lane's own
/// `LinAlgPatterns.(|BlasL3|_|)` over the shared loop description. Attached
/// to the EXACT ArrVal `applyToArr` returned (reference identity, below), so
/// any view, re-shape or re-wrap of it -- a `{ a with ... }` copy is a new
/// object -- loses the attachment and takes the ordinary nest.
type private SyrkCandidate =
    { /// The contracted operand X (dense p x k, Float64), as emitted once.
      Operand: ArrVal
      P: int64
      K: int64
      /// The kernel's scalar tail, applied to the finished sums.
      Scale: Blade.LinAlgPatterns.NestScale option }

/// ArrVal -> its tile-core candidacy, by REFERENCE (ConditionalWeakTable
/// keys on object identity and never keeps an ArrVal alive).
let private syrkCandidates =
    System.Runtime.CompilerServices.ConditionalWeakTable<ArrVal, SyrkCandidate>()

/// A deferred value the antisym or simplex TILE CORE may materialize (M7 /
/// M8): the C++ lane's `LinAlgPatterns.(|RowContraction|_|)` matched this
/// nest's shared loop description (the same matcher, so the lanes cannot
/// disagree about the shape). Attached by reference identity, as above.
type private RowTileCandidate =
    { /// The one input array: Z (p x c x n) for the pair, X (p x n) for the simplex.
      Operand: ArrVal
      Shape: Blade.LinAlgPatterns.RowContractionShape
      P: int64
      /// The component extent c (the pair only; 0 for the simplex).
      Comps: int64
      /// The sample extent n.
      N: int64
      Scale: Blade.LinAlgPatterns.NestScale option }

let private rowTileCandidates =
    System.Runtime.CompilerServices.ConditionalWeakTable<ArrVal, RowTileCandidate>()

let private newCtx (callables: Map<IRId, IRCallable>) (facts: ModuleFacts) =
    { Globals = ResizeArray()
      PoolScopes = ResizeArray()
      AnyFrees = false
      StringPool = Dictionary()
      CoordTables = Dictionary()
      Externs = SortedSet(StringComparer.Ordinal)
      Funcs = ResizeArray()
      Callables = callables
      Emitted = Dictionary()
      Pending = Queue()
      Counter = 0
      Facts = facts
      FoldFmf = false
      FoldOperandWrites = false
      DeclSpan = Blade.Ast.noSpan
      DeclName = ""
      UsedAttrGroups = SortedSet()
      Slots = Dictionary()
      ArrSlots = Dictionary()
      NameSlots = Dictionary()
      NameArrSlots = Dictionary()
      LoopObjs = HashSet()
      GroupKeys = Dictionary()
      TupleSlots = Dictionary()
      Forced = HashSet()
      BindingValues = Map.empty
      DeferredLetValues = Dictionary()
      DomainMapBodies = Dictionary(HashIdentity.Reference)
      Inlining = HashSet()
      ModuleReads = Dictionary()
      PublishWanted = HashSet()
      Published = Dictionary()
      ModuleNames = Map.empty
      DeferredModule = Dictionary()
      InFunction = false
      StmtStart = -1
      StmtSlots = Dictionary()
      HoistedIds = HashSet()
      Spine = HashSet(HashIdentity.Reference)
      SpineCallArgs = HashSet(HashIdentity.Reference)
      SpineNode = null
      SpinePositional = false
      StmtPositionalReads = HashSet()
      StmtForceOrder = []
      TupleRoots = HashSet(HashIdentity.Reference)
      TupleTail = null
      BreakTargets = Stack()
      Allocas = ResizeArray()
      Body = ResizeArray()
      MemberTables = Dictionary() }

let private nextN (c: Ctx) = c.Counter <- c.Counter + 1; c.Counter
let private freshReg (c: Ctx) = sprintf "%%r%d" (nextN c)
let private freshLbl (c: Ctx) (tag: string) = $"{tag}{nextN c}"
let private ln (c: Ctx) (s: string) = c.Body.Add("  " + s)
let private lbl (c: Ctx) (l: string) = c.Body.Add(l + ":")
let private need (c: Ctx) (decl: string) = c.Externs.Add decl |> ignore

/// Claim an attribute group and render its `#N` reference (empty when function
/// attributes are switched off).
let private attrRef (c: Ctx) (g: int) : string =
    if not (factEnabled factFnAttrs) then ""
    else
        c.UsedAttrGroups.Add g |> ignore
        $" #{g}"

/// The uniform parameter decoration (` noundef`), or nothing when parameter
/// attributes are off.
let private paramAttr () : string =
    if factEnabled factParamAttrs then " noundef" else ""

// ---------------------------------------------------------------------------
// Fast-math flags
// ---------------------------------------------------------------------------

/// The flag text one arithmetic instruction is born with, in LLVM's canonical
/// order, with the leading space `BinInstr.Flags` expects.
///
/// TWO INDEPENDENT LICENSES, and neither is a blanket:
///   * `reassoc nsz` -- only inside a fold whose kernel passed
///     `foldReorderLicensed`, in every build but BLADE_FP_REPRO=1 (the
///     default since 2026-10-17). `nsz` rides with
///     `reassoc` because a reassociated sum's intermediate zeros are exactly
///     what the reordering may resign, and LLVM's reduction vectorizer wants
///     both before it will split the chain into lanes.
///   * `contract` -- unless `BLADE_FP_CONTRACT` turns it off (the g++ lane's
///     default; see `contractLicensed`).
/// Integer instructions get nothing: `nsw`/`nuw` would be a WRAPAROUND claim
/// this lane has not proved (Blade's Int64 arithmetic wraps -- in the C++
/// lane because Build.optFlags passes `-fwrapv`, in the interpreter because
/// .NET integer arithmetic is unchecked -- and no overflow analysis exists to
/// say otherwise).
let private fmfFor (c: Ctx) (ty: Sc) : string =
    if ty <> ScF64 || not (factEnabled factFmf) then ""
    else
        let parts =
            [ if c.FoldFmf then
                  yield "reassoc"
                  yield "nsz"
              if contractLicensed () then yield "contract" ]
        if List.isEmpty parts then "" else " " + String.concat " " parts

/// May a fold through this kernel be reassociated? The SAME two-part gate the
/// C++ lane spends on its K-lane forms (`CodeGenBinding.fs`): the declared or
/// derived license, and the build's polarity (`fpReassocEnabled`: on unless
/// BLADE_FP_REPRO=1). Sharing the predicate is what
/// keeps the two lanes' numeric policy one policy -- an answer that reassociates
/// in one lane and not the other would make the differential gate report
/// drift that is really a policy split.
let private foldFmfLicensed (c: Ctx) (cl: IRCallable) : bool =
    Blade.CodeGenState.fpReassocEnabled () && Blade.CodeGenExprSupport.foldReorderLicensed cl
    && not c.FoldOperandWrites

/// May FMF DECORATE the kernel application? Only when that application IS the
/// combining instruction -- a recognized builtin op, where `applyKernel` emits
/// exactly one `fadd`/`fmul`. A lambda or named kernel INLINES its body inside
/// the `withFoldFmf` scope, so `reassoc nsz` would land on ALL of the body's
/// arithmetic -- e.g. the `fmul` in `a + b * b` -- and the fold license covers
/// reordering whole kernel APPLICATIONS across iterations, never the interior
/// of one application. Such kernels still fold correctly; they just carry no
/// flags (the C++ lane's K-lane forms reassociate structurally for the same
/// reason).
let private foldFmfDecorable (c: Ctx) (cl: IRCallable) : bool =
    foldFmfLicensed c cl && (Blade.CodeGenExprSupport.foldKernelBuiltinOp cl).IsSome

/// Emit `f` with the licensed-fold flag scope open. Wrapped around the KERNEL
/// APPLICATION only, never around the element read that feeds it: on a
/// deferred operand that read runs the map kernel, whose arithmetic carries no
/// license at all.
let private withFoldFmf (c: Ctx) (licensed: bool) (f: unit -> 'a) : 'a =
    if not licensed then f ()
    else
        let saved = c.FoldFmf
        c.FoldFmf <- true
        try f () finally c.FoldFmf <- saved

// ---------------------------------------------------------------------------
// The shim's C ABI
// ---------------------------------------------------------------------------

/// One shim symbol's declaration shape.
type private ShimFn =
    { Ret: string
      /// Return-value attributes. `noalias align 64` on the allocator is the
      /// ONLY `noalias` this lane emits: that pointer is freshly allocated, so
      /// it provably aliases nothing live, and `blade_alloc_cells` aligns to 64
      /// bytes by construction. A read operand never gets one -- the measured
      /// payoff there was 0-3% (plan section 1) against a miscompile risk the
      /// red team priced at "Rust needed six years".
      RetAttrs: string
      /// Parameter TYPES; `noundef` is added uniformly (every argument this
      /// emitter passes is a real value -- there is no undef/poison channel).
      Args: string list
      Group: int }

/// Every symbol the shim exports, in ONE table.
///
/// Not a literal at each call site, for a reason that already bites: a
/// `declare` line is deduplicated by EXACT TEXT, and two sites needing
/// `blade_out_str` (`outStr` and `outVal`) that spelled its attributes
/// differently would emit the symbol twice and clang would reject the module.
/// One table also means the fact layer decorates each symbol in exactly one
/// place.
let private shimTable : Map<string, ShimFn> =
    let io ret args = { Ret = ret; RetAttrs = ""; Args = args; Group = grpExternReturns }
    Map.ofList
        [ "blade_now", io "double" []
          "blade_panic", { Ret = "void"; RetAttrs = ""; Args = [ "ptr" ]; Group = grpShimPanic }
          "blade_panic_at", { Ret = "void"; RetAttrs = ""; Args = [ "ptr"; "ptr"; "i64" ]; Group = grpShimPanic }
          "blade_alloc_cells",
          { Ret = "ptr"; RetAttrs = "noalias align 64"; Args = [ "i64"; "i64" ]; Group = grpShimAlloc }
          "blade_free", { Ret = "void"; RetAttrs = ""; Args = [ "ptr" ]; Group = grpShimFree }
          // The arithmetic contract (docs/formalism.md section 2.4): the twins of
          // blade_rt::idiv / imod / ipow / f2i and blade_arith::fpow.
          // Each takes the fault's source position (file ptr or null, line) for
          // the panic's `--> file:line`, read only on the cold path.
          "blade_idiv", { Ret = "i64"; RetAttrs = ""; Args = [ "i64"; "i64"; "ptr"; "i64" ]; Group = grpShimArith }
          "blade_imod", { Ret = "i64"; RetAttrs = ""; Args = [ "i64"; "i64"; "ptr"; "i64" ]; Group = grpShimArith }
          "blade_ipow", { Ret = "i64"; RetAttrs = ""; Args = [ "i64"; "i64"; "ptr"; "i64" ]; Group = grpShimArith }
          "blade_f2i64", { Ret = "i64"; RetAttrs = ""; Args = [ "double"; "ptr"; "i64" ]; Group = grpShimArith }
          "blade_fpow", io "double" [ "double"; "double" ]
          "blade_fpowi", io "double" [ "double"; "i64" ]
          // The rank-1 compound's keyed read (G9): the kept ordinal of `key`
          // in the ascending position table, -1 when it was not kept.
          "blade_compact1_rank", { Ret = "i64"; RetAttrs = ""; Args = [ "ptr"; "i64"; "i64" ]; Group = grpShimArgRead }
          // Membership hoisting (round 3, B-T1; blade_member.h through the
          // shim): build into the caller's slot (allocates the tables, and
          // frees a half-made one when an allocation fails -- so not
          // `nofree` -- never panics, always returns: grpShimFree's claims),
          // probe (reads only), free the tables.
          "blade_member_init_i64", { Ret = "void"; RetAttrs = ""; Args = [ "ptr"; "ptr"; "i64"; "i64" ]; Group = grpShimFree }
          "blade_member_has_i64", { Ret = "i32"; RetAttrs = ""; Args = [ "ptr"; "i64" ]; Group = grpShimRead }
          "blade_member_fini_i64", { Ret = "void"; RetAttrs = ""; Args = [ "ptr" ]; Group = grpShimFree }
          // the build's report: actual vs preferred representation (stderr
          // under BLADE_MEMBER_VERBOSE=1)
          "blade_member_note_llvm", io "void" [ "ptr"; "ptr"; "ptr" ]
          // aggregate-only grouping's key -> group map (blade_member.h,
          // Optimize.planGroupAggregate): init and the find-or-insert may
          // allocate, grow and free their own tables and always return;
          // record / count only read the map; fini frees it
          "blade_gkmap_init_llvm", { Ret = "i32"; RetAttrs = ""; Args = [ "ptr"; "i64" ]; Group = grpShimFree }
          "blade_gkmap_group_llvm", { Ret = "i64"; RetAttrs = ""; Args = [ "ptr"; "i64" ]; Group = grpShimFree }
          "blade_gkmap_record_llvm", { Ret = "ptr"; RetAttrs = ""; Args = [ "ptr"; "i64" ]; Group = grpShimRead }
          "blade_gkmap_count_llvm", { Ret = "i64"; RetAttrs = ""; Args = [ "ptr" ]; Group = grpShimRead }
          "blade_gkmap_fini_llvm", { Ret = "void"; RetAttrs = ""; Args = [ "ptr" ]; Group = grpShimFree }
          "blade_out_str", io "void" [ "ptr" ]
          "blade_out_i64", io "void" [ "i64" ]
          "blade_out_f64", io "void" [ "double" ]
          "blade_out_bool", io "void" [ "i32" ]
          "blade_print_i64", io "void" [ "ptr"; "i64" ]
          "blade_print_f64", io "void" [ "ptr"; "double" ]
          "blade_print_bool", io "void" [ "ptr"; "i32" ]
          "blade_print_str", io "void" [ "ptr"; "ptr" ]
          "blade_print_completed", io "void" [ "ptr"; "double" ] ]

/// The shim symbols that can panic: the panic path itself and the
/// arithmetic-contract helpers. Shadow-frame elision (`resolveFrames`) treats
/// a call to one of these as reaching a panic; tests/LlvmTests.fs holds the
/// shim source to this set.
let shimPanicSymbols : Set<string> =
    shimTable
    |> Map.filter (fun _ f -> f.Group = grpShimPanic || f.Group = grpShimArith)
    |> Map.keys |> Set.ofSeq

/// Declare one shim symbol, decorated with whatever fact classes are on.
let private needShim (c: Ctx) (name: string) : unit =
    let f =
        match Map.tryFind name shimTable with
        | Some f -> f
        | None -> refuse ($"the shim symbol '{name}', which has no declaration in shimTable")
    let args = f.Args |> List.map (fun t -> t + paramAttr ()) |> String.concat ", "
    let ret =
        if factEnabled factParamAttrs && f.RetAttrs <> "" then f.RetAttrs + " " + f.Ret else f.Ret
    need c ($"declare {ret} @{name}({args}){(attrRef c f.Group)}")

/// Reserve an entry-block slot. Every `alloca` in this file goes through
/// here; see `Ctx.Allocas` for why none may be emitted in place.
let private allocaOf (c: Ctx) (llType: string) : string =
    let reg = freshReg c
    c.Allocas.Add($"  {reg} = alloca {llType}")
    reg

// ---------------------------------------------------------------------------
// Pool-tracking scopes (the arena model's edge, plan section 0.7/0.8 item 3)
// ---------------------------------------------------------------------------

/// Open a tracking scope at the CURRENT body position. Everything `allocPool`
/// hands out while this scope is innermost gets a tracking slot; the matching
/// `popPoolScope` frees what the slots still hold.
let private pushPoolScope (c: Ctx) : unit =
    c.PoolScopes.Add { At = c.Body.Count
                       Pools = ResizeArray()
                       InlineId = None
                       KnownIds = HashSet(c.ArrSlots.Keys) }

/// The pool now held by binding `id` OUTLIVES every open scope the binding
/// PREDATES (`KnownIds` holds it: the scope opened after the binding, so the
/// binding's later reads run after that scope's exit frees). Those scopes --
/// the YOUNGER ones -- must not free it: their tracking slot for the pool is
/// nulled. The scope the binding was BORN in (the innermost one that does not
/// know it) stays the OWNER: a slot of its own is not touched, and a pool a
/// younger scope allocated is HANDED to it through a fresh slot of the owner's
/// (stored here, reset at the owner's entry, freed at the owner's exit), so
/// the binding's storage dies with the binding, exactly once. With no owner --
/// a module binding inside main's loop scopes, a function parameter -- the
/// pool escapes every scope, as before (gap G78: the null used to land in
/// EVERY scope that tracked the pool, the owning function scope included, so
/// a function's tape and its cotangent, read by a younger adjoint loop, were
/// never freed). Emitting stores (rather than editing the records) is what
/// makes a CONDITIONAL escape correct -- they land only on executions that
/// actually took the escaping path.
let private keepPool (c: Ctx) (id: IRId) (poolReg: string) : unit =
    let owner = c.PoolScopes |> Seq.filter (fun s -> not (s.KnownIds.Contains id)) |> Seq.tryLast
    for scope in List.ofSeq c.PoolScopes do
        if scope.KnownIds.Contains id then
            for (p, slot) in List.ofSeq scope.Pools do
                if p = poolReg then
                    ln c ($"store ptr null, ptr {slot}")
                    match owner with
                    | Some o when not (o.Pools |> Seq.exists (fun (q, _) -> q = poolReg)) ->
                        let ownSlot = allocaOf c "ptr"
                        ln c ($"store ptr {poolReg}, ptr {ownSlot}   ; G78: handed to the scope that owns the binding")
                        o.Pools.Add(poolReg, ownSlot)
                    | _ -> ()

/// Start a POSITIONAL statement inside the current statement (Ctx.Spine):
/// from here on a module force hoists to THIS point -- after the
/// code already emitted for the statement -- with the slot tables as they now
/// stand as the hoist's soundness baseline (see materializeExpr).
let private positionalStatement (c: Ctx) : unit =
    c.StmtStart <- c.Body.Count
    c.StmtSlots <- Dictionary(c.ArrSlots)
    c.HoistedIds.Clear()
    c.SpineNode <- null

/// The C++ lane's collectDeferredPositionalReads (CodeGenFusion.fs), the SAME
/// WALK SHAPE line for line, over this lane's deferred state: the bases of
/// the deferred values `root` reads POSITIONALLY (an IRIndex / IRExtent /
/// IRContains base, an assignment RHS, a call argument, every prodsum
/// operand, a fold over a deferred WRAPPER) in first-read order, each once.
/// Like the C++ walk it does not descend combinator / loop-object children
/// (they force their own inputs) and walks only the CHILDREN of a slice /
/// curry / subset. `isDeferred` says whether an id is a still-deferred value
/// in this lane; `isWrapper` whether a fold operand names a deferred choice /
/// fallback (CodeGenFusion.isDeferredWrapperOperand).
let private collectPositionalReadsLl (isDeferred: IRId -> bool) (isWrapper: IRExpr -> bool) (root: IRExpr) : IRExpr list =
    let ordered = ResizeArray<IRExpr>()
    let seen = HashSet<IRId>()
    let note (baseExpr: IRExpr) =
        match baseExpr with
        | IRVar (id, _) when isDeferred id ->
            if seen.Add id then ordered.Add baseExpr
        | _ -> ()
    let rec walk (e: IRExpr) =
        match e with
        | IRIndex (a, idxs, _) -> note a; walk a; List.iter walk idxs
        | IRExtent (a, _) -> note a; walk a
        | IRContains (a, v) -> note a; walk a; walk v
        | IRLet (_, v, b) -> walk v; walk b
        | IRForRange (_, lo, hi, body) -> walk lo; walk hi; walk body
        | IRAssign (t, v) -> walk t; note v; walk v
        | IRSequence es -> List.iter walk es
        | IRConstraintCheck (cnd, _, _, _) -> walk cnd
        | IRBreakIf cnd -> walk cnd
        | IRReplicate (count, body) -> walk count; walk body
        | IRGuard (cnd, b) -> walk cnd; walk b
        | IRBinOp (_, _, l, r, _) -> walk l; walk r
        | IRUnaryOp (_, x) -> walk x
        | IRIf (cnd, t, el) -> walk cnd; walk t; walk el
        | IRApp (f, args, _) -> walk f; List.iter (fun a -> note a; walk a) args
        | IRProdSum es -> List.iter (fun a -> note a; walk a) es
        | IRTuple es | IRArrayLit (es, _) | IRStack es | IRZip es -> List.iter walk es
        | IRJoin (es, _) -> List.iter walk es
        | IRComplex (re, im) -> walk re; walk im
        | IRFma (a, b, d) -> walk a; walk b; walk d
        | IRRandAt (_, args, w) -> List.iter walk args; w |> Option.iter (fst >> walk)
        | IRFieldAccess (o, _) -> walk o
        | IRTupleProj (x, _, _) -> walk x
        | IRTupleCons (h, t) -> walk h; walk t
        | IRTupleDecons t -> walk t
        | IRSlice (a, _, s, e2) -> walk a; walk s; walk e2
        | IRCurry (a, i, _) -> walk a; walk i
        | IRSubset (a, _, s, l) -> walk a; walk s; walk l
        | IRPolyIndex (p, i) -> walk p; walk i
        | IRPolyTail (p, _) -> walk p
        | IRPure x | IRCompute x -> walk x
        | IRMatch (s, cases) ->
            walk s
            cases |> List.iter (fun cs -> Option.iter walk cs.Guard; walk cs.Body)
        | IRReduce (a, k, initOpt) ->
            if isWrapper a then note a
            walk a; walk k; Option.iter walk initOpt
        | IRReduceCompute (comp, k, init) -> walk comp; walk k; walk init
        | _ -> ()
    walk root
    List.ofSeq ordered

/// Install the spine of a statement rooted at `root` (see Ctx.Spine), or
/// clear it (None). `inFunction`: a function body's spine, whose nodes are
/// each a POSITIONAL statement (a private map is stored at the statement that
/// first forces it -- the lane had no earlier form to keep) and whose calls
/// may all take the ordered path. Main's spine keeps the BASE force boundary
/// -- an ordinary positional force inside a top-level statement is still
/// hoisted to the START of the whole statement, as the C++ lane's
/// statement pre-pass does (review codex-diff-W4B-B-2 finding 1) -- so its
/// nodes only mark where they are (Ctx.SpineNode, for the block-alias site)
/// and only the statement's ROOT call takes the ordered path (the C++ lane's
/// genOrderedCallBinding, a binding whose root is the call).
let private setSpine (c: Ctx) (root: IRExpr option) (inFunction: bool) : unit =
    c.SpineNode <- null
    c.SpinePositional <- inFunction
    c.Spine <- HashSet(HashIdentity.Reference)
    c.SpineCallArgs <- HashSet(HashIdentity.Reference)
    let addCall (e: IRExpr) =
        match e with
        | IRApp (_, args, _) -> c.SpineCallArgs.Add(box args) |> ignore
        | _ -> ()
    let addNode (e: IRExpr) =
        c.Spine.Add(box e) |> ignore
        if inFunction then addCall e
    let rec go (e: IRExpr) =
        addNode e
        match e with
        | IRLet (_, v, body) -> addNode v; go body
        | IRSequence xs -> xs |> List.iter go
        | _ -> ()
    c.StmtPositionalReads <- HashSet()
    c.StmtForceOrder <- []
    c.TupleRoots <- HashSet(HashIdentity.Reference)
    c.TupleTail <- null
    match root with
    | Some r ->
        go r
        addCall r
        if not inFunction then
            // the C++ pre-pass's positional-read set (genLetChainBinding: a
            // chain ending in a TUPLE pre-passes its lets' values only, the
            // tuple's reads are its own; any other statement, the whole value)
            let isDeferred (id: IRId) =
                match c.ArrSlots.TryGetValue id with
                | true, { Src = APool _ } -> false
                | true, _ -> true
                | _ -> false
            let rec isWrapper (e: IRExpr) =
                match e with
                | IRVar (id, _) when isDeferred id ->
                    (match Map.tryFind id c.BindingValues with
                     | Some (IRChoice _ | IRFallback _) -> true
                     | Some (IRVar _ as inner) -> isWrapper inner
                     | _ -> false)
                | _ -> false
            let rec unroll (e: IRExpr) (acc: IRExpr list) =
                match e with
                | IRLet (_, v, body) -> unroll body (v :: acc)
                | fin -> (List.rev acc, fin)
            let order =
                match r with
                | IRLet _ ->
                    (match unroll r [] with
                     | (lets, IRTuple _) -> lets |> List.collect (collectPositionalReadsLl isDeferred isWrapper)
                     | _ -> collectPositionalReadsLl isDeferred isWrapper r)
                | _ -> collectPositionalReadsLl isDeferred isWrapper r
            let seen = HashSet<IRId>()
            c.StmtForceOrder <-
                order |> List.filter (fun x -> match x with IRVar (id, _) -> seen.Add id | _ -> false)
            for x in c.StmtForceOrder do
                match x with IRVar (id, _) -> c.StmtPositionalReads.Add id |> ignore | _ -> ()
            let rec tail (e: IRExpr) = match e with IRLet (_, _, body) -> tail body | e -> e
            (match r with IRTuple _ -> c.TupleRoots.Add(box r) |> ignore | _ -> ())
            (match r, tail r with
             | IRLet _, (IRTuple _ as t) ->
                 c.TupleRoots.Add(box t) |> ignore
                 c.TupleTail <- box t
             | _ -> ())
    | None -> ()

/// Entering a spine node (Ctx.Spine) -- on its FIRST entry only, which is
/// the straight-line one (the node is dropped from the set, so a producer
/// that re-emits part of a spine value later, inside a nest, never moves
/// anything there) -- records it as Ctx.SpineNode and, on a function body's
/// spine, starts a positional statement at it.
let private spineStatement (c: Ctx) (e: IRExpr) : bool =
    if c.Spine.Count > 0 && c.StmtStart >= 0 && c.Spine.Remove(box e) then
        if c.SpinePositional then positionalStatement c
        c.SpineNode <- box e
        true
    else false

/// Close the innermost scope. With `emitFrees`, each tracking slot is loaded
/// and freed -- a null slot (branch never taken, or pool kept) frees
/// nothing. `keepReg` spares its own entry TEXTUALLY: a tracking slot only
/// ever holds its own allocation or null, and two live allocations cannot
/// share an address, so no runtime compare is needed. The scope-entry null
/// resets are inserted where the scope opened. Without `emitFrees` the
/// records are simply dropped -- today's arena.
let private popPoolScope (c: Ctx) (keepReg: string option) (emitFrees: bool) : unit =
    let scope = c.PoolScopes.[c.PoolScopes.Count - 1]
    c.PoolScopes.RemoveAt(c.PoolScopes.Count - 1)
    let freed = scope.Pools |> Seq.filter (fun (p, _) -> Some p <> keepReg) |> Seq.toList
    if emitFrees && not (List.isEmpty freed) then
        c.AnyFrees <- true
        needShim c "blade_free"
        let resets =
            freed |> List.map (fun (_, slot) -> $"  store ptr null, ptr {slot}")
        c.Body.InsertRange(scope.At, resets)
        for (_, slot) in freed do
            let p = freshReg c
            ln c ($"{p} = load ptr, ptr {slot}")
            ln c ($"call void @blade_free(ptr {p})")

/// A double as its EXACT bit pattern. `.ll` accepts `double 0x<16 hex>`, and
/// that spelling is the only one immune to the decimal round-trip question
/// the C++ lane had to solve separately (floatToCppLiteral's "R" format).
let private f64Const (x: float) =
    sprintf "0x%016X" (uint64 (BitConverter.DoubleToInt64Bits x))

/// Intern a string as a private constant and return its symbol. `ptr` is the
/// symbol itself under opaque pointers -- no getelementptr needed.
let private stringGlobal (c: Ctx) (s: string) : string =
    match c.StringPool.TryGetValue s with
    | true, g -> g
    | _ ->
        let bytes = Encoding.UTF8.GetBytes s
        let name = $"@.blade.str.{c.StringPool.Count}"
        let sb = StringBuilder()
        for b in bytes do
            // `\` and `"` would end the token; anything outside printable
            // ASCII goes out as \XX so the file stays byte-exact.
            if b >= 0x20uy && b < 0x7Fuy && b <> 0x22uy && b <> 0x5Cuy then sb.Append(char b) |> ignore
            else sb.AppendFormat("\\{0:X2}", b) |> ignore
        sb.Append("\\00") |> ignore
        c.Globals.Add($"{name} = private unnamed_addr constant [{bytes.Length + 1} x i8] c\"{(sb.ToString())}\"")
        c.StringPool.[s] <- name
        name

// ---------------------------------------------------------------------------
// Shadow frames (the twin of the C++ lane's BLADE_FRAME)
// ---------------------------------------------------------------------------
//
// A panic prints the Blade call stack, innermost first: `  at <SourceName>`
// per live frame, after the `-->` line. A frame opens at a function body's
// entry and around every INLINED kernel application (this lane inlines
// kernels and capturing callees instead of calling them, where the C++ lane
// calls a framed function), so both lanes push the same callables.
//
// A frame is observable only while a panic is reachable under it, so one
// that reaches none is dropped -- exactly CodeGen.resolveShadowFrames' rule,
// and for the same reason: the frame sits in the hot loop of every inlined
// kernel. Reachability is whole-module (a body panics through what it
// calls), so emission writes MARKER lines and `resolveFrames` decides each
// one from the emitted text once every function exists. Unlike the C++
// scan, nothing here is Unknown: this lane refuses indirect calls, so every
// call names a module function or a shim symbol, and the shim's panicking
// symbols are the ones shimTable files under the panic and arithmetic groups.
// (The allocator's out-of-memory exit is deliberately not one: the C++ lane's
// `new` fails without a trace too, and counting it would frame every body
// that allocates.)

let private framePushMark = ";__BLADE_FRAME_PUSH__ "
let private framePopMark = ";__BLADE_FRAME_POP__"

/// Open a frame named `name` (an IRCallable.SourceName) at the body position.
let private framePush (c: Ctx) (name: string) = c.Body.Add(framePushMark + name)

/// Close the innermost open frame. Every push is matched by exactly one pop
/// on the body's fall-through path; a panic leaves through `exit` and never
/// pops.
let private framePop (c: Ctx) = c.Body.Add framePopMark

/// LLVM identifiers take [A-Za-z0-9_$.-]; Blade names are already tame but a
/// module-qualified or generated name is not guaranteed to be.
let private sanitizeSym (s: string) =
    let sb = StringBuilder()
    for ch in s do
        if Char.IsLetterOrDigit ch || ch = '_' then sb.Append ch |> ignore
        else sb.Append '_' |> ignore
    let out = sb.ToString()
    if out = "" then "fn" else out

// ---------------------------------------------------------------------------
// Conversions and promotion
//
// These mirror C++'s implicit conversions at the SAME seams the C++ emitter
// relies on them (assignment to a declared type, argument passing, return),
// because that is what makes the two lanes agree on `let n: Int64 = 2 ^ 3`
// (pow returns double; the binding truncates).
// ---------------------------------------------------------------------------

let private coerce (c: Ctx) (target: Sc) (v: Val) : Val =
    if v.Ty = target then v
    else
        let dest = freshReg c
        let conv opcode =
            ln c (renderConv { Dest = dest; Opcode = opcode; From = v.Ty; Value = v.Reg; To = target })
            { Reg = dest; Ty = target }
        match v.Ty, target with
        | ScI64, ScF64 -> conv "sitofp"
        | ScBool, ScI64 -> conv "zext"
        | ScBool, ScF64 -> conv "uitofp"
        // C++ narrows double -> integral by truncation toward zero, which is
        // exactly fptosi.
        | ScF64, ScI64 -> conv "fptosi"
        | ScI64, ScBool ->
            ln c (renderCmp { Dest = dest; Kind = "icmp"; Pred = "ne"; Ty = ScI64; Lhs = v.Reg; Rhs = "0" })
            { Reg = dest; Ty = ScBool }
        | ScF64, ScBool ->
            // `(bool)x` in C++ is `x != 0`, and NaN converts to true -- `une`
            // (unordered-or-not-equal) is the predicate that says so.
            ln c (renderCmp { Dest = dest; Kind = "fcmp"; Pred = "une"; Ty = ScF64; Lhs = v.Reg; Rhs = f64Const 0.0 })
            { Reg = dest; Ty = ScBool }
        | _ ->
            refuse ($"no conversion from {(llTy v.Ty)} to {(llTy target)}")

/// The binary-operand common type. Mirrors `IR.promoteElemType` restricted to
/// the widths v1 carries.
let private promote (a: Sc) (b: Sc) : Sc =
    match a, b with
    | ScStr, _ | _, ScStr -> refuse "an operator applied to String operands"
    | ScVoid, _ | _, ScVoid -> refuse "an operator applied to a unit operand"
    | ScF64, _ | _, ScF64 -> ScF64
    | ScI64, _ | _, ScI64 -> ScI64
    | _ -> ScBool

// ---------------------------------------------------------------------------
// Math intrinsics
// ---------------------------------------------------------------------------

/// The libm-backed unary intrinsics. `GradCommon.mathIntrinsics` is the
/// language's list; lgamma/digamma are deliberately ABSENT here because they
/// are Blade's OWN hand-rolled series (blade_runtime.hpp), kept bit-identical
/// with the interpreter -- routing them to libm would silently change results,
/// so the lane refuses the program instead.
let private libmUnary : Map<string, string> =
    [ "exp", "exp"; "log", "log"; "log10", "log10"; "sqrt", "sqrt"
      "sin", "sin"; "cos", "cos"; "tan", "tan"
      "sinh", "sinh"; "cosh", "cosh"; "tanh", "tanh"
      "asin", "asin"; "acos", "acos"; "atan", "atan"
      "floor", "floor"; "ceil", "ceil"
      // `std::abs` on a double IS fabs; typeOf pins the result Float64.
      "abs", "fabs" ]
    |> Map.ofList

/// Transcendental libm entry points whose call sites carry `nobuiltin`: the
/// arithmetic contract (docs/formalism.md section 2.4) says their value is
/// the platform libm's AT RUN TIME, never a compile-time fold. LLVM's constant
/// folder evaluates a recognized libcall on constant arguments with the HOST
/// compiler's own libm -- which happens to be the target's on this toolchain,
/// but is not the contract. `nobuiltin` on the call site withdraws the
/// libcall recognition for exactly that call. sqrt/floor/ceil/fabs are
/// absent: they are correctly rounded, so a fold is the runtime value, and
/// sqrt must stay recognizable to vectorize.
let private libmNoFold : Set<string> =
    Set.ofList [ "exp"; "log"; "log10"; "sin"; "cos"; "tan"; "sinh"; "cosh"; "tanh"
                 "asin"; "acos"; "atan"; "atan2"; "pow" ]

/// The integer literal an IR operand IS (`3`, `-1`), if it is one.
let private literalIntOf (e: IRExpr) : int64 option =
    match e with
    | IRLit (IRLitInt n) -> Some n
    | IRUnaryOp (IRNeg, IRLit (IRLitInt n)) -> Some (0L - n)
    | _ -> None

/// Call a shim symbol (declared through shimTable) that returns a value.
let private callShim (c: Ctx) (name: string) (ret: Sc) (args: (Sc * string) list) : Val =
    needShim c name
    let dest = freshReg c
    ln c (renderCall { Dest = Some dest; RetTy = ret; Callee = "@" + name; Args = args })
    { Reg = dest; Ty = ret }

// ---------------------------------------------------------------------------
// Static-extent arrays: dense pools and packed simplex pools
//
// STORAGE MODEL (plan section 5, "flat pool + baked extents"): one contiguous
// pool per array, addressed by constant-folded GEPs. No Iliffe skeleton --
// the pointer-of-pointers the C++ lane builds is a way to spell `arr[i][j]`
// in C++, not a layout, and the pool order it indexes is exactly the order
// below.
//
// A COMPACT (symmetric / antisymmetric) group stores only its canonical
// simplex, in the same ascending-lex order: row i of a rank-2 group occupies
// `n - i - strict` contiguous cells starting at `rowBase(i)`. That is why the
// C++ lane's printer walks `arr[i][j]` with the shrinking bound `extents[1] -
// i` and why this lane's printer must too -- the two are pinned against each
// other by every corpus EXPECT over a symmetric array.
//
// Still refused by name: Hermitian (constraint-coupled cells), wreath/OrbIdx
// (a wreath pool has no tile multiset), and compact groups of rank 3+ (the
// prisms of plan section 3.2).
// ---------------------------------------------------------------------------

/// The in-pool spelling of an element. Bools narrow to `i8` because `i1` has
/// no settled memory layout to share with the C shim, and a Blade `Bool`
/// array must be readable by the printer as a C `int`.
let private poolTy = function
    | ScI64 -> "i64"
    | ScF64 -> "double"
    | ScBool -> "i8"
    | ScStr -> "ptr"
    | ScVoid -> refuse "an array of unit elements"

let private poolElemBytes = function
    | ScBool -> 1L
    | _ -> 8L

/// Does a rank-r simplex of extent n keep the emitted offset arithmetic inside
/// i64? `emitBinomConst` lays down the FALLING FACTORIAL and divides at the
/// end, so the intermediate -- at most (n+r)^r -- overflows well before the
/// binomial itself would. Checked in floating point because the whole point is
/// to answer without overflowing. Rank 2 admits n up to ~3e9, rank 3 ~2e6,
/// rank 5 ~6000: comfortably past any extent whose pool would fit in memory,
/// so this refuses only genuinely absurd shapes -- and it refuses them by
/// declining the GROUP, which makes it a clean whole-program fallback to the
/// C++ lane rather than a miscompile.
let private simplexFitsI64 (r: int) (n: int64) : bool =
    let mutable acc = 1.0
    for _ in 1 .. r do acc <- acc * float (n + int64 r)
    acc < 9.0e18

/// Project ONE index record onto a shape group, or None when this lane has no
/// storage for it.
let private groupOfIndexType (ix: IRIndexType) : Grp option =
    match ix.IxKind, ix.Extent with
    | IxKSparse, IRSparseKeys (SkDomain plan) -> Some (GDomain plan)
    | _ ->
    if ix.IxKind <> IxKPlain || not (List.isEmpty ix.Dependencies) then None
    else
        match Blade.IRPrint.tryEvalIntIR ix.Extent with
        | None -> None
        | Some n ->
            match ix.Symmetry, ix.Rank with
            | SymNone, 1 -> Some (GDense n)
            // ANY rank: the offset closed form, the serial nest and the
            // canonicalizing read are all rank-parametric, so rank 3+ needs no
            // arm of its own -- only the arithmetic-range check above.
            | SymSymmetric, r when r >= 2 && simplexFitsI64 r n -> Some (GSym (r, n))
            | SymAntisymmetric, r when r >= 2 && simplexFitsI64 r n -> Some (GAnti (r, n))
            // Hermitian, wreath, and out-of-range simplices land here.
            | _ -> None

/// Project an IRType onto the static-extent array universe. Everything this
/// returns None for is a refusal at the use site, WITH ITS OWN WORDING:
/// "not an array shape" is never a useful diagnostic on its own.
let private arrayShapeOf (t: IRType) : (Sc * Grp list) option =
    match Blade.IR.stripUnits t with
    | ArrayElem arr when not (List.isEmpty arr.IndexTypes) ->
        let elemOk = scalarTyOf arr.ElemType
        let groups = arr.IndexTypes |> List.map groupOfIndexType
        match elemOk with
        | Some e when e <> ScVoid && groups |> List.forall Option.isSome ->
            Some (e, groups |> List.map Option.get)
        | _ -> None
    | _ -> None

/// The refusal every array-shaped site shares, so one unsupported index type
/// reads the same wherever it is met.
let private requireArray (what: string) (t: IRType) : Sc * Grp list =
    match arrayShapeOf t with
    | Some s -> s
    | None ->
        refuse ($"{what} has type {(Blade.IRPrint.ppIRType t)} -- the llvm lane handles arrays over static Idx<n> axes and rank-2 Sym/Antisym groups only")

/// The RAGGED-PAIR array type: a static plain outer axis over a ragged-family
/// (or DepIdx) inner. Deliberately NOT folded into arrayShapeOf: every
/// existing dense/compact site keeps refusing ragged by name, and only the
/// sites that grew a ragged story consult this. Answers (element, rows).
let private raggedArrShape (t: IRType) : (Sc * int64) option =
    match Blade.IR.stripUnits t with
    | ArrayElem arr ->
        (match arr.IndexTypes with
         | [ outer; inner ]
             when (outer.IxKind = IxKPlain || outer.IxKind = IxKDepOuter)
                  && outer.Symmetry = SymNone && outer.Rank = 1
                  && (isRaggedFamilyKind inner.IxKind || inner.IxKind = IxKDepInner) ->
             (match scalarTyOf arr.ElemType, Blade.IRPrint.tryEvalIntIR outer.Extent with
              | Some e, Some n when e <> ScVoid -> Some (e, n)
              | _ -> None)
         | _ -> None)
    | _ -> None

/// `for (i = 0; i < n; i++)`, with the counter in an entry-block slot so
/// mem2reg turns it back into the induction variable it is. `body` may emit
/// its own control flow: the increment lands in whatever block is current
/// when it returns, and terminates it.
let private emitCountedLoopTo (c: Ctx) (bound: string) (body: string -> unit) : unit =
    let iv = allocaOf c "i64"
    ln c ($"store i64 0, ptr {iv}")
    let lCond = freshLbl c "loop.cond"
    let lBody = freshLbl c "loop.body"
    let lEnd = freshLbl c "loop.end"
    ln c (sprintf "br label %%%s" lCond)
    lbl c lCond
    let i = freshReg c
    ln c ($"{i} = load i64, ptr {iv}")
    let t = freshReg c
    ln c (renderCmp { Dest = t; Kind = "icmp"; Pred = "slt"; Ty = ScI64; Lhs = i; Rhs = bound })
    ln c (renderBr { Cond = t; IfTrue = lBody; IfFalse = lEnd })
    lbl c lBody
    body i
    let i2 = freshReg c
    ln c ($"{i2} = load i64, ptr {iv}")
    let i3 = freshReg c
    ln c (renderBin { Dest = i3; Opcode = "add"; Flags = ""; Ty = ScI64; Lhs = i2; Rhs = "1" })
    ln c ($"store i64 {i3}, ptr {iv}")
    ln c (sprintf "br label %%%s" lCond)
    lbl c lEnd

/// The literal-trip-count form. Every rectangular loop in this file is one of
/// these, which is what "static extents only" buys: the trip count is a
/// constant the vectorizer reads straight off the compare.
let private emitCountedLoop (c: Ctx) (n: int64) (body: string -> unit) : unit =
    emitCountedLoopTo c (string n) body

/// A row-major nest over `extents`, innermost last, handing the body one
/// index register per level.
let private emitNest (c: Ctx) (extents: int64 list) (body: Val list -> unit) : unit =
    let rec go acc rest =
        match rest with
        | [] -> body (List.rev acc)
        | n :: tl -> emitCountedLoop c n (fun i -> go ({ Reg = i; Ty = ScI64 } :: acc) tl)
    go [] extents

let private i64Bin (c: Ctx) (opcode: string) (lhs: string) (rhs: string) : string =
    let d = freshReg c
    ln c (renderBin { Dest = d; Opcode = opcode; Flags = ""; Ty = ScI64; Lhs = lhs; Rhs = rhs })
    d

/// `a + b` with the literal-zero identity folded at EMISSION time. The tile
/// arithmetic below is full of `+ 0` (the serial triangle's row origin, a
/// symmetric group's strict offset), and emitting them would bury the shape of
/// the loop under instructions clang deletes anyway -- a .ll nobody can read
/// is a .ll nobody checks.
let private i64Add (c: Ctx) (a: string) (b: string) : string =
    if a = "0" then b elif b = "0" then a else i64Bin c "add" a b

/// The first pool cell of storage row `i` in a rank-2 compact group of extent
/// `n`: `i * (2n + 1 - 2*strict - i) / 2`, which is
/// `SimplexBlocksCore.rowBase2` emitted. Closed form rather than a running
/// counter on purpose -- it makes every row's addressing independent of every
/// other's, which is what lets a brick's rows vectorize and (later)
/// parallelize. One of the two factors is always even, so the halving is
/// exact.
let private emitRowBase2 (c: Ctx) (n: int64) (strict: bool) (i: string) : string =
    let k = 2L * n + 1L - (if strict then 2L else 0L)
    let t = i64Bin c "sub" (string k) i
    let m = i64Bin c "mul" i t
    i64Bin c "sdiv" m "2"

/// An operand that is a decimal literal, or None. Constant folding at
/// emission is not cosmetic here: the rank-r offset arithmetic below is full
/// of binomials of a CONSTANT (`n` minus a literal lower bound), and folding
/// them turns a chain of six instructions into one immediate.
let private tryLitI64 (s: string) : int64 option =
    match System.Int64.TryParse(s, System.Globalization.NumberStyles.AllowLeadingSign,
                                System.Globalization.CultureInfo.InvariantCulture) with
    | true, v -> Some v
    | _ -> None

/// Emit a STATIC ragged group's offsets table as a module constant and answer
/// its symbol. `constant`, never `global`: LLVM then knows no store anywhere
/// can touch it, so consecutive rows share one load (the previous row's hi IS
/// this row's lo) and a small outer loop can fold the loads away entirely.
let private raggedTableGlobal (c: Ctx) (offsets: int64[]) : string =
    let name = $"@.blade.roff.{nextN c}"
    let body = offsets |> Array.map (sprintf "i64 %d") |> String.concat ", "
    c.Globals.Add($"{name} = private unnamed_addr constant [{offsets.Length} x i64] [{body}], align 64")
    name

/// offsets[i] as an operand: folded to a literal when the table and the index
/// are both compile-time; one gep + load otherwise -- the SAME two lines for
/// the static global and the dynamic pointer.
let private raggedOffAt (c: Ctx) (table: RaggedTable) (i: string) : string =
    match table, tryLitI64 i with
    | RtStatic (offsets, _), Some k when k >= 0L && k < int64 offsets.Length ->
        string offsets.[int k]
    | _ ->
        let sym = match table with RtStatic (_, s) -> s | RtDynamic p -> p
        let g = freshReg c
        ln c ($"{g} = getelementptr inbounds i64, ptr {sym}, i64 {i}")
        let v = freshReg c
        ln c ($"{v} = load i64, ptr {g}")
        v

/// The length of row i: offsets[i+1] - offsets[i], folded when it can be.
let private raggedLenAt (c: Ctx) (table: RaggedTable) (i: string) : string =
    match table, tryLitI64 i with
    | RtStatic (offsets, _), Some k when k >= 0L && k + 1L < int64 offsets.Length ->
        string (offsets.[int k + 1] - offsets.[int k])
    | _ ->
        let ip1 = i64Bin c "add" i "1"
        let hi = raggedOffAt c table ip1
        let lo = raggedOffAt c table i
        i64Bin c "sub" hi lo

// ---------------------------------------------------------------------------
// CLOSED-FORM ENUMERABLE DOMAINS (docs/plans/structural/06, bench gap G4)
// ---------------------------------------------------------------------------
// A `range<R>` over a static struct whose conjuncts are linear is ONE shape
// group, `GDomain plan`, spanning the struct's r fields. Its coordinates are
// the FIELD VALUES (not storage coordinates: there is no box), its cells are
// the plan's solutions in lex order, and a cell's pool offset is its lex rank.
// Iteration is the affine nest (`emitDomainNest`): field k over [max of its
// lower forms, min of its upper forms], the ends affine in the outer fields,
// with one running counter for the offset -- the same nest the C++ lane's
// genDomainLoopHeader writes and the interpreter's `enumerateDomain` walks.
// A KEYED read (`R((i, j))`) of a materialized domain array needs the rank
// from coordinates: the per-level PREFIX TABLES below (static constants, one
// entry per valid prefix of the outer r-1 fields -- never one per cell),
// behind a membership check that aborts BL8006 like the C++ key index does.

/// `c + sum a_j x_j` from the outer fields' registers.
let private domainAffineReg (c: Ctx) (f: DomainAffine) (xs: string[]) : string =
    let mutable acc = string f.Const
    for (j, a) in f.Coefs do
        let t = if a = 1L then xs.[j] else i64Bin c "mul" xs.[j] (string a)
        acc <- (match tryLitI64 acc with
                | Some 0L -> t
                | _ -> i64Bin c "add" acc t)
    acc

/// A level's end: the max of its lower forms, the min of its upper forms
/// (signed compare + select; a constant pair folds at emission).
let private domainBoundReg (c: Ctx) (isLower: bool) (b: DomainBound) (xs: string[]) : string =
    let regs = b |> List.distinct |> List.map (fun f -> domainAffineReg c f xs)
    match regs with
    | [] -> refuse "an enumerated-domain level with no bound"
    | first :: rest ->
        rest |> List.fold (fun acc r ->
            match tryLitI64 acc, tryLitI64 r with
            | Some a, Some b -> string (if isLower then max a b else min a b)
            | _ ->
                let t = freshReg c
                ln c (renderCmp { Dest = t; Kind = "icmp"; Pred = (if isLower then "sgt" else "slt"); Ty = ScI64; Lhs = acc; Rhs = r })
                let s = freshReg c
                ln c ($"{s} = select i1 {t}, i64 {acc}, i64 {r}")
                s) first

/// THE AFFINE NEST over a domain's solutions, lex order, handing the body the
/// field values and the cell's offset (its lex rank: a running counter, so
/// the nest pays one add per cell and no table).
let private emitDomainNest (c: Ctx) (plan: DomainPlan) (body: Val list -> Val -> unit) : unit =
    let lv = Array.ofList plan.Levels
    let r = lv.Length
    let cnt = allocaOf c "i64"
    ln c ($"store i64 0, ptr {cnt}")
    let rec go (k: int) (xs: string list) =
        if k = r then
            let off = freshReg c
            ln c ($"{off} = load i64, ptr {cnt}")
            body (xs |> List.map (fun x -> { Reg = x; Ty = ScI64 })) { Reg = off; Ty = ScI64 }
            let next = i64Bin c "add" off "1"
            ln c ($"store i64 {next}, ptr {cnt}")
        else
            let arr = Array.ofList xs
            let lo = domainBoundReg c true lv.[k].Lo arr
            let hi = domainBoundReg c false lv.[k].Hi arr
            // trips = hi - lo + 1; an empty interval gives trips <= 0, which the
            // counted loop's signed `<` runs zero times.
            let trips =
                match tryLitI64 lo, tryLitI64 hi with
                | Some l, Some h -> string (h - l + 1L)
                | _ -> i64Bin c "add" (i64Bin c "sub" hi lo) "1"
            emitCountedLoopTo c trips (fun t ->
                let v = match tryLitI64 lo with Some 0L -> t | _ -> i64Bin c "add" lo t
                go (k + 1) (xs @ [ v ]))
    go 0 []

/// A band's DIAGONAL walk (Types.domainDiagonal): k over [KLo, KHi], then the
/// outer field i over [max(ILo, JLo + k), min(IHi, JHi + k)] with j = i - k.
/// No cell counter: only a licensed fold over the virtual map takes it, and a
/// fold has no output to address.
let private emitDomainDiagonalNest (c: Ctx) (dg: DomainDiagonal) (body: Val list -> unit) : unit =
    emitCountedLoopTo c (string (dg.KHi - dg.KLo + 1L)) (fun t ->
        let k = if dg.KLo = 0L then t else i64Bin c "add" t (string dg.KLo)
        let lo = domainBoundReg c true [ { Const = dg.ILo; Coefs = [] }; { Const = dg.JLo; Coefs = [ (0, 1L) ] } ] [| k |]
        let hi = domainBoundReg c false [ { Const = dg.IHi; Coefs = [] }; { Const = dg.JHi; Coefs = [ (0, 1L) ] } ] [| k |]
        let trips = i64Bin c "add" (i64Bin c "sub" hi lo) "1"
        emitCountedLoopTo c trips (fun u ->
            let i = i64Bin c "add" lo u
            let j = i64Bin c "sub" i k
            body [ { Reg = i; Ty = ScI64 }; { Reg = j; Ty = ScI64 } ]))

/// The prefix tables of a plan's lex rank: `firsts.[k].[q]` is the rank, among
/// the level-(k+1) prefixes, of the first child of level-k prefix q (prefixes
/// of the OUTER r-1 fields only; rank 1 needs none). Walked at compile time.
let private domainPrefixTables (plan: DomainPlan) : int64[][] =
    let lv = Array.ofList plan.Levels
    let r = lv.Length
    let tables = ResizeArray<int64[]>()
    if r >= 2 then
        let full (p: int64[]) = Array.append p (Array.zeroCreate (r - p.Length))
        let lo0 = Blade.Types.domainLower lv.[0].Lo (Array.zeroCreate r)
        let hi0 = Blade.Types.domainUpper lv.[0].Hi (Array.zeroCreate r)
        let mutable level : int64[] list = [ for x in lo0 .. hi0 -> [| x |] ]
        for k in 0 .. r - 2 do
            let firsts = ResizeArray<int64>()
            let children = ResizeArray<int64[]>()
            let mutable running = 0L
            for q in level do
                firsts.Add running
                let pf = full q
                let lo = Blade.Types.domainLower lv.[k + 1].Lo pf
                let hi = Blade.Types.domainUpper lv.[k + 1].Hi pf
                if hi >= lo then
                    running <- running + (hi - lo + 1L)
                    if k < r - 2 then
                        for x in lo .. hi do children.Add (Array.append q [| x |])
            tables.Add (firsts.ToArray())
            level <- List.ofSeq children
    tables.ToArray()

/// The pool offset of the cell at FIELD VALUES `xs`, after the membership
/// check (every field within its interval at that prefix) -- a key outside the
/// domain aborts BL8006 with the C++ key index's message.
let private domainRankReg (c: Ctx) (plan: DomainPlan) (xs: Val list) : string =
    let lv = Array.ofList plan.Levels
    let r = lv.Length
    if List.length xs <> r then
        refuse ($"an enumerated domain of {r} field(s) addressed by {List.length xs} coordinate(s)")
    let regs = xs |> List.map (fun v -> v.Reg) |> Array.ofList
    // Every level's interval depends on the coordinates only, so the whole
    // membership test is formed BEFORE any table load can go out of range.
    let bounds = [| for k in 0 .. r - 1 -> (domainBoundReg c true lv.[k].Lo regs, domainBoundReg c false lv.[k].Hi regs) |]
    let mutable ok = "true"
    for k in 0 .. r - 1 do
        let (lo, hi) = bounds.[k]
        let ge = freshReg c
        ln c (renderCmp { Dest = ge; Kind = "icmp"; Pred = "sge"; Ty = ScI64; Lhs = regs.[k]; Rhs = lo })
        let le = freshReg c
        ln c (renderCmp { Dest = le; Kind = "icmp"; Pred = "sle"; Ty = ScI64; Lhs = regs.[k]; Rhs = hi })
        let both = freshReg c
        ln c ($"{both} = and i1 {ge}, {le}")
        if ok = "true" then ok <- both
        else
            let a = freshReg c
            ln c ($"{a} = and i1 {ok}, {both}")
            ok <- a
    let lOk = freshLbl c "dom.ok"
    let lBad = freshLbl c "dom.bad"
    ln c (renderBr { Cond = ok; IfTrue = lOk; IfFalse = lBad })
    lbl c lBad
    needShim c "blade_panic"
    let m = stringGlobal c "error[BL8006]: index out of bounds: a key outside the sparse key set"
    ln c (renderCall { Dest = None; RetTy = ScVoid; Callee = "@blade_panic"; Args = [ ScStr, m ] })
    ln c "unreachable"
    lbl c lOk
    let tables = domainPrefixTables plan
    let mutable p = i64Bin c "sub" regs.[0] (fst bounds.[0])
    for k in 1 .. r - 1 do
        let sym = raggedTableGlobal c tables.[k - 1]
        let g = freshReg c
        ln c ($"{g} = getelementptr inbounds i64, ptr {sym}, i64 {p}")
        let first = freshReg c
        ln c ($"{first} = load i64, ptr {g}")
        let within = i64Bin c "sub" regs.[k] (fst bounds.[k])
        p <- i64Bin c "add" first within
    p

/// `C(x, m)` for a RUNTIME x and a compile-time m: the falling factorial
/// x(x-1)...(x-m+1) over the constant m!. Exact in integers -- a product of m
/// consecutive integers is always divisible by m! -- so this needs no rounding
/// care. Folds outright when x is a literal.
///
/// The callers only ever evaluate this where `x >= m` holds by construction
/// (see `emitPrefixTerm`), which is what lets the plain falling factorial
/// stand in for `SimplexBlocksCore.binom`'s clamped-to-zero branch.
let private emitBinomConst (c: Ctx) (x: string) (m: int) : string =
    if m <= 0 then "1"
    elif m = 1 then x
    else
        match tryLitI64 x with
        | Some v -> string (Blade.SimplexBlocksCore.binom v m)
        | None ->
            let mutable acc = x
            for j in 1 .. m - 1 do
                acc <- i64Bin c "mul" acc (i64Bin c "sub" x (string j))
            let mutable f = 1L
            for j in 2 .. m do f <- f * int64 j
            // `exact`: the quotient has no remainder by the identity above, so
            // LLVM may lower the division to a shift and a multiply by the
            // modular inverse of the odd part instead of the signed-division
            // fix-up sequence -- this runs once per level of every canonical
            // compact read (bench G34).
            let d = freshReg c
            ln c (renderBin { Dest = d; Opcode = "sdiv"; Flags = " exact"; Ty = ScI64; Lhs = acc; Rhs = string f })
            d

/// `SimplexBlocksCore.prefixTerm` emitted: the rank contributed by advancing
/// coordinate k of a rank-r group from its canonical lower bound `lo` by the
/// STORAGE offset `s` (so the absolute coordinate is `lo + s`).
///
///     strict:     C(n-lo,   m+1) - C(n-lo-s,   m+1)
///     symmetric:  C(n-lo+m, m+1) - C(n-lo-s+m, m+1)     (m = r-k-1)
///
/// TWO PROPERTIES CARRY THE WHOLE DESIGN. At the last level (k = r-1, m = 0)
/// the degree is 1 and the term is exactly `s` -- no arithmetic at all, so the
/// innermost run is affine in its loop counter and therefore contiguous in the
/// pool at EVERY rank. And every term is invariant under the levels inside it,
/// so a nest hoists each one to its own level and pays O(1) per cell.
///
/// `x >= m+1` holds at both evaluation points for any canonical tuple (the
/// canonical bound leaves at least `m` coordinates' worth of room above
/// `i_k`), which is why `emitBinomConst`'s unclamped falling factorial is
/// sound here.
let private emitPrefixTerm (c: Ctx) (strict: bool) (n: int64) (r: int) (k: int)
                           (lo: string) (s: string) : string =
    let m = r - k - 1
    if m = 0 then s
    else
        let d = if strict then 0L else int64 m
        let hi =
            match tryLitI64 lo with
            | Some l -> string (n + d - l)
            | None -> i64Bin c "sub" (string (n + d)) lo
        let lowEnd = i64Bin c "sub" hi s
        i64Bin c "sub" (emitBinomConst c hi (m + 1)) (emitBinomConst c lowEnd (m + 1))

/// The offset of a cell inside ONE group, from that group's STORAGE
/// coordinates. A dense axis is its own coordinate (no instruction at all); a
/// compact group of rank r sums `emitPrefixTerm` over its levels, threading
/// the canonical lower bound (`lo_0 = 0`, `lo_k = i_{k-1} + strict`).
///
/// At rank 2 this is `rowBase(i) + p` with the row base spelled as
/// `C(n,2) - C(n-i,2)` instead of `i*(2n-i-1)/2` -- the same value (the
/// property pins assert it) by the one formula that also serves ranks 3+.
let private grpOffset (c: Ctx) (g: Grp) (idxs: Val list) : string =
    match g, idxs with
    | GDense _, [ i ] -> i.Reg
    | GDense _, _ -> refuse "a dense axis indexed by more than one coordinate"
    | GDynDense _, [ i ] -> i.Reg
    | GDynDense _, _ -> refuse "a runtime-extent axis indexed by more than one coordinate"
    // STORAGE coordinates: the kept ordinal. (The keyed read `S(k)` resolves
    // its key to this ordinal first -- emitRaw's compound arm.)
    | GCompact _, [ i ] -> i.Reg
    | GCompact _, _ -> refuse "a compound axis indexed by more than one coordinate"
    // A ragged cell is offsets[row] + element: one table operand and one add,
    // folded outright when the row is a literal against a static table.
    | GRagged (_, table), [ i; j ] -> i64Add c (raggedOffAt c table i.Reg) j.Reg
    | GRagged _, _ -> refuse "a ragged group addressed by other than (row, element) coordinates"
    | GDomain plan, xs -> domainRankReg c plan xs
    | _ ->
        let r = grpRank g
        let n = grpExtent g
        let strict = grpStrict g
        if List.length idxs <> r then
            refuse ($"a rank-{r} compact index group addressed by {List.length idxs} coordinates")
        let sInc = if strict then 1L else 0L
        let mutable lo = "0"
        let mutable acc = "0"
        idxs |> List.iteri (fun k (sk: Val) ->
            acc <- i64Add c acc (emitPrefixTerm c strict n r k lo sk.Reg)
            // The next level's canonical floor: i_k + strict = lo + s_k + strict.
            lo <- i64Add c (i64Add c lo sk.Reg) (string sInc))
        acc

/// Pool offset from STORAGE coordinates, one per axis. The Horner chain over
/// groups: `((off0 * cells1 + off1) * cells2 + off2) ...`. For an all-dense
/// shape every group offset is the bare index and the chain is exactly the
/// row-major `((i0*E1 + i1)*E2 + i2)` it always was -- byte-identical
/// emission, which is what keeps the dense goldens pinned across this change.
let private storageOffset (c: Ctx) (groups: Grp list) (idxs: Val list) : string =
    let rec split (gs: Grp list) (xs: Val list) =
        match gs with
        | [] -> []
        | g :: tl ->
            let r = grpRank g
            (g, xs |> List.truncate r) :: split tl (xs |> List.skip (min r xs.Length))
    match idxs with
    | [] -> "0"
    | _ ->
        let parts = split groups idxs
        match parts with
        | [] -> "0"
        | (g0, x0) :: rest ->
            let mutable acc = grpOffset c g0 x0
            for (g, xs) in rest do
                let off = grpOffset c g xs
                let m = i64Bin c "mul" acc (string (grpCells g))
                acc <- i64Bin c "add" m off
            acc

let private gepCell (c: Ctx) (elem: Sc) (basePtr: string) (off: string) : string =
    let p = freshReg c
    ln c ($"{p} = getelementptr inbounds {(poolTy elem)}, ptr {basePtr}, i64 {off}")
    p

let private loadCell (c: Ctx) (elem: Sc) (ptr: string) : Val =
    let raw = freshReg c
    ln c ($"{raw} = load {(poolTy elem)}, ptr {ptr}")
    match elem with
    | ScBool ->
        let b = freshReg c
        ln c ($"{b} = icmp ne i8 {raw}, 0")
        { Reg = b; Ty = ScBool }
    | _ -> { Reg = raw; Ty = elem }

let private storeCell (c: Ctx) (elem: Sc) (ptr: string) (v: Val) : unit =
    match elem with
    | ScBool ->
        let w = freshReg c
        ln c ($"{w} = zext i1 {v.Reg} to i8")
        ln c ($"store i8 {w}, ptr {ptr}")
    | _ -> ln c ($"store {(poolTy elem)} {v.Reg}, ptr {ptr}")

/// Sibling maps (Optimize.planSiblingMaps): per group, (committed, the stashed
/// members' (first centre, producer, pool), completed) for this emission.
let private siblingLlvmStorage =
    System.Threading.AsyncLocal<System.Collections.Generic.Dictionary<int, bool * (int64 * ArrVal * string) list * bool>>()
let private siblingLlvmCell () : System.Collections.Generic.Dictionary<int, bool * (int64 * ArrVal * string) list * bool> =
    let v = siblingLlvmStorage.Value
    if isNull (box v) then
        let fresh = System.Collections.Generic.Dictionary<int, bool * (int64 * ArrVal * string) list * bool>()
        siblingLlvmStorage.Value <- fresh
        fresh
    else v

/// An UNINITIALIZED pool of `n` cells -- `n` an OPERAND, so a runtime-sized
/// ragged pool and a literal-sized dense one are the same call.
///
/// THE INVARIANT EVERY CALLER DISCHARGES: the pool's every cell is written
/// before any cell is read. Each call site is a complete fill by
/// construction -- `materialize`'s four nests each cover their whole shape,
/// `copyArr` copies cell-for-cell, and the two literal fills store exactly
/// `total` cells behind a count guard. Zeros the LANGUAGE promises (a
/// recursive array's unbuilt prefix, `guard`'s else arm) are explicit stores
/// in the IR both lanes share, so they hold here without allocator help --
/// the C++ lane's pools are uninitialized `new T[]` for the same reason. The
/// allocator used to memset anyway, which cost one full write pass per pool
/// inside the timed region (the llvm lane's "13-20% IR-shape headroom" of
/// plan-simplex-blocked-compute.md section 0b was almost entirely this).
let private allocPool (c: Ctx) (elem: Sc) (n: string) : string =
    needShim c "blade_alloc_cells"
    let p = freshReg c
    ln c (renderCall { Dest = Some p; RetTy = ScStr; Callee = "@blade_alloc_cells"
                       Args = [ ScI64, n; ScI64, string (poolElemBytes elem) ] })
    // Inside a tracking scope (a function body, an IRForRange trip), record
    // the pool so the scope's exit can free it -- through a slot, so a pool
    // allocated on one branch frees without a dominance argument.
    if c.PoolScopes.Count > 0 then
        let slot = allocaOf c "ptr"
        ln c ($"store ptr {p}, ptr {slot}")
        c.PoolScopes.[c.PoolScopes.Count - 1].Pools.Add(p, slot)
    p

/// Read one cell, at STORAGE coordinates. A pool read is a GEP + load; a
/// producer read RUNS the deferred kernel at this index, which is what makes
/// an unforced computation cost nothing until somebody asks for a value.
let private readCell (c: Ctx) (a: ArrVal) (idxs: Val list) : Val =
    match a.Src with
    | APool p -> loadCell c a.Elem (gepCell c a.Elem p (storageOffset c a.Groups idxs))
    | AVirt f -> f c idxs
    | AVirtFlat f -> f c { Reg = storageOffset c a.Groups idxs; Ty = ScI64 }

/// Read cell number `k` of the pool, ignoring shape entirely. The
/// flat-elementwise path: legal exactly when the caller knows the ordinal
/// means the same cell in this array as in the one it is co-iterating.
let private readFlat (c: Ctx) (a: ArrVal) (k: Val) : Val =
    match a.Src with
    | APool p -> loadCell c a.Elem (gepCell c a.Elem p k.Reg)
    | AVirtFlat f -> f c k
    | AVirt _ -> refuse "a flat read of a coordinate-bearing producer"

/// Can this value answer a flat cell-ordinal read without materializing?
let private hasFlatRead (a: ArrVal) : bool =
    match a.Src with
    | APool _ | AVirtFlat _ -> true
    | AVirt _ -> false

/// THE PER-COORDINATE OFFSET TABLES of the canonicalizing read (bench G34).
/// The pool offset of a SORTED rank-r tuple regroups into one term per
/// coordinate (`SimplexBlocksCore.coordTerm`, proved equal to the level form
/// on every canonical cell by the property pins), so for a static extent the
/// read is r-1 loads from module constants and r-1 adds -- where the level
/// form spends 2(r-1) runtime binomials (multiplies and divisions) per read.
/// That arithmetic made the LLVM lane's logical checksum walk
/// throughput-bound at ~38 integer instructions a cell, against the C++
/// lane's two row-pointer loads (`canon_fold` + the nested walk). Bounded
/// so the tables stay cache-resident: (r-1) x n x 8 bytes, 32 KB per table
/// at the bound.
let private coordTableMaxExtent = 4096L

/// May this group's canonical reads go through the tables?
let private coordTabulable (g: Grp) : bool =
    match g with
    | GSym (r, n) | GAnti (r, n) -> r >= 2 && n >= 1L && n <= coordTableMaxExtent
    | _ -> false

/// Table k of a (strict, n, r) group as a module constant, emitted once per
/// module. `constant`: LLVM knows nothing writes it.
let private coordTableGlobal (c: Ctx) (strict: bool) (n: int64) (r: int) (k: int) : string =
    let key = $"{strict}:{n}:{r}:{k}"
    match c.CoordTables.TryGetValue key with
    | true, g -> g
    | _ ->
        let name = $"@.blade.ctab.{c.CoordTables.Count}"
        let body =
            [| for x in 0L .. n - 1L -> "i64 " + string (Blade.SimplexBlocksCore.coordTerm strict n r k x) |]
            |> String.concat ", "
        c.Globals.Add($"{name} = private unnamed_addr constant [{n} x i64] [{body}], align 64")
        c.CoordTables.[key] <- name
        name

/// A compact group's pool offset from its SORTED absolute coordinates:
/// sum_{k < r-1} table_k[i_k] + i_(r-1). A literal coordinate folds.
let private coordTableOffset (c: Ctx) (g: Grp) (sorted: string[]) : string =
    let r = grpRank g
    let n = grpExtent g
    let strict = grpStrict g
    let mutable acc = sorted.[r - 1]
    for k in 0 .. r - 2 do
        let term =
            match tryLitI64 sorted.[k] with
            | Some x -> string (Blade.SimplexBlocksCore.coordTerm strict n r k x)
            | None ->
                let tab = coordTableGlobal c strict n r k
                let p = freshReg c
                ln c ($"{p} = getelementptr inbounds [{n} x i64], ptr {tab}, i64 0, i64 {sorted.[k]}")
                let v = freshReg c
                ln c ($"{v} = load i64, ptr {p}")
                v
        acc <- i64Add c acc term
    acc

/// Total stored cells of a shape, as an OPERAND: a literal for every static
/// shape (including a static-table ragged pair, whose offsets[rows] folds),
/// one load for a dynamic-table ragged pair.
let private shapeCellsOp (c: Ctx) (gs: Grp list) : string =
    match gs with
    | [ GRagged (rows, table) ] -> raggedOffAt c table rows
    | [ GDynDense len ] -> len
    | [ GCompact (len, _) ] -> len
    | _ -> string (shapeCells gs)

/// The single extent of a rank-1 shape, as an OPERAND: a literal for a dense
/// axis, the carried length operand for a peeled ragged row. Loops take it
/// through `emitCountedLoopTo`, which is what makes the two the same shape.
let private soleExtentOp (a: ArrVal) : string =
    match a.Groups with
    | [ GDense n ] -> string n
    | [ GDynDense len ] -> len
    | [ GCompact (len, _) ] -> len
    | _ -> refuse "a rank-1 operand with no single extent"

/// The `pos` operand of a rank-1 compound whose kept positions were ELIDED
/// (compaction fusion v2, T4b: the demand census proved no consumer reads
/// them). Its only reader, the keyed-read arm, refuses it as an internal
/// error, so no lookup through it is ever emitted.
let private compactNoPositions = "null"

/// The rank-1 compound's kept-position table, when `a` is one.
let private compactPosOf (a: ArrVal) : (string * string) option =
    match a.Groups with
    | [ GCompact (len, pos) ] -> Some (len, pos)
    | _ -> None

/// Read at ABSOLUTE coordinates -- the ones a user writes in `S(2)(1)`. For a
/// dense shape this IS `readCell` (identical emission, so the dense goldens do
/// not move). For a compact group it is the canonicalizing read the C++ lane
/// spells `canon_fold` + `ReadTransform`: sort the pair, left-justify it into
/// storage coordinates, then apply the class's character --
///   * symmetric: none, the mirror cell holds the same value;
///   * antisymmetric: NEGATE when the pair was swapped, and answer ZERO on
///     the diagonal, which is not stored at all.
/// The diagonal of a strict group is NOT stored, so a diagonal read must
/// never compute the diagonal's own address: at i = j = n-1 the last row of
/// a strict triangle is EMPTY and `rowBase(n-1) + 0` is one cell past the
/// pool (invisible to value checks because the value is discarded; exposed
/// to the allocator whenever the 64-byte rounding leaves no slack --
/// smallest extent n = 16, where C(16,2)*8 = 960 is a multiple of 64). The
/// guard is BRANCHLESS: the storage coordinates are redirected to cell 0 on
/// the diagonal -- always inside the allocation -- and the fetched value is
/// discarded by select, so the read stays a straight-line select chain the
/// loop vectorizer can if-convert.
///
/// With several strict groups the character composes: the value is zero when
/// ANY group sits on its diagonal, and the sign flips once per SWAPPED group
/// (an XOR, not the last group's flag).
let private canonRead (c: Ctx) (a: ArrVal) (idxs: Val list) : Val =
    if not (a.Groups |> List.exists grpCompact) then readCell c a idxs
    else
    let mutable rest = idxs
    let mutable diags : string list = []
    let mutable swaps : string list = []
    // THE TABLE FORM (G34, `coordTableOffset`): a flat-readable source whose
    // every compact group tabulates reads at ONE pool offset computed from
    // the sorted coordinates, never the storage coordinates below.
    let useTables =
        (match a.Src with APool _ | AVirtFlat _ -> true | AVirt _ -> false)
        && a.Groups |> List.forall (fun g -> match g with GDense _ -> true | _ -> coordTabulable g)
    let sortedParts = ResizeArray<Grp * string[]>()
    let storage =
        [ for g in a.Groups do
            let r = grpRank g
            let mine = rest |> List.truncate r
            rest <- rest |> List.skip r
            match g, mine with
            | GDense _, [ i ] ->
                sortedParts.Add((g, [| i.Reg |]))
                if not useTables then yield i
            | _ when grpCompact g && List.length mine = r ->
                // CANONICALIZE BY SORTING NETWORK, at any rank. A bubble pass
                // over r coordinates is r(r-1)/2 compare-exchanges, each a
                // compare and two selects -- straight-line, no branch, and at
                // r = 2 it is exactly the single exchange this used to be.
                //
                // The ANTISYMMETRIC CHARACTER falls out of the same network:
                // the permutation's sign is the PARITY of the exchanges it
                // performed (a transposition is odd), so xor-ing the swap
                // flags gives the sign for any rank -- the rank-2 "negate when
                // swapped" rule generalized, with no per-rank table.
                let arr = Array.ofList (mine |> List.map (fun (v: Val) -> v.Reg))
                let mutable swapFlags = []
                for pass in 0 .. r - 2 do
                    for q in 0 .. r - 2 - pass do
                        let x = arr.[q]
                        let y = arr.[q + 1]
                        let gt = freshReg c
                        ln c (renderCmp { Dest = gt; Kind = "icmp"; Pred = "sgt"; Ty = ScI64; Lhs = x; Rhs = y })
                        let lo = freshReg c
                        ln c ($"{lo} = select i1 {gt}, i64 {y}, i64 {x}")
                        let hi = freshReg c
                        ln c ($"{hi} = select i1 {gt}, i64 {x}, i64 {y}")
                        arr.[q] <- lo
                        arr.[q + 1] <- hi
                        swapFlags <- gt :: swapFlags
                if grpStrict g then
                    // Sorted, so a repeat is ADJACENT: r-1 equality tests
                    // decide "not stored" (the strict diagonal, at any rank --
                    // for r >= 3 that is every tuple with any two equal
                    // coordinates, not merely i = j).
                    for q in 0 .. r - 2 do
                        let eq = freshReg c
                        ln c (renderCmp { Dest = eq; Kind = "icmp"; Pred = "eq"; Ty = ScI64; Lhs = arr.[q]; Rhs = arr.[q + 1] })
                        diags <- eq :: diags
                    swaps <- swapFlags @ swaps
                sortedParts.Add((g, Array.copy arr))
                // Sorted ABSOLUTE coordinates -> STORAGE coordinates:
                // s_0 = i_0, s_k = i_k - i_{k-1} - strict. On a strict repeat
                // some s_k is negative, which is a dead select arm: the whole
                // tuple is REDIRECTED to cell 0 below before any address is
                // computed.
                if not useTables then
                    let sInc = if grpStrict g then 1L else 0L
                    for q in 0 .. r - 1 do
                        if q = 0 then yield { Reg = arr.[0]; Ty = ScI64 }
                        else
                            let d = i64Bin c "sub" arr.[q] arr.[q - 1]
                            yield { Reg = (if sInc = 0L then d else i64Bin c "sub" d (string sInc)); Ty = ScI64 }
            | _ -> refuse "an absolute read of an index group this lane cannot canonicalize" ]
    // The table form's flat offset: the Horner chain over groups
    // (`storageOffset`'s), each compact group's own offset from its sorted
    // coordinates.
    let flatOffset () =
        let groupOff (g: Grp, sorted: string[]) = if grpCompact g then coordTableOffset c g sorted else sorted.[0]
        let parts = List.ofSeq sortedParts
        let mutable acc = groupOff parts.Head
        for (g, sorted) in parts.Tail do
            let m = i64Bin c "mul" acc (string (grpCells g))
            acc <- i64Bin c "add" m (groupOff (g, sorted))
        acc
    match diags with
    | [] when useTables -> readFlat c a { Reg = flatOffset (); Ty = ScI64 }
    | [] -> readCell c a storage
    | _ ->
        let fold1 (op: string) (regs: string list) =
            regs |> List.reduce (fun x y ->
                let d = freshReg c
                ln c ($"{d} = {op} i1 {x}, {y}")
                d)
        let eqAny = fold1 "or" diags
        let swXor = fold1 "xor" swaps
        let ety = a.Elem
        let zero =
            match ety with
            | ScF64 -> f64Const 0.0
            | ScI64 -> "0"
            | _ -> refuse "an antisymmetric array of non-numeric elements"
        // BRANCHLESS guarded read. On a diagonal the strict groups' rows may
        // be EMPTY (i = n-1), so the unredirected offset lands one past the
        // pool -- but instead of branching around the load, REDIRECT every
        // storage coordinate to cell 0, which is always inside the allocation
        // (the shim's minimum pool is one 64-byte line, so even a zero-cell
        // antisym pool has a readable cell 0), and discard the fetched value
        // by select. A branch here defeated the loop vectorizer on every
        // antisym mirror-read loop (CantVectorizeInstruction, 2026-08-18
        // census); the select chain if-converts, and the sym mirror fold that
        // uses only selects vectorizes under the reassoc license.
        let v =
            if useTables then
                let off = flatOffset ()
                let r = freshReg c
                ln c ($"{r} = select i1 {eqAny}, i64 0, i64 {off}")
                coerce c ety (readFlat c a { Reg = r; Ty = ScI64 })
            else
            let redirected =
                storage |> List.map (fun v ->
                    let r = freshReg c
                    ln c ($"{r} = select i1 {eqAny}, i64 0, i64 {v.Reg}")
                    { Reg = r; Ty = ScI64 })
            coerce c ety (readCell c a redirected)
        let negated =
            match ety with
            | ScF64 ->
                let d = freshReg c
                ln c ($"{d} = fneg double {v.Reg}")
                d
            | _ -> i64Bin c "sub" "0" v.Reg
        let signed = freshReg c
        ln c ($"{signed} = select i1 {swXor}, {(llTy ety)} {negated}, {(llTy ety)} {v.Reg}")
        let final = freshReg c
        ln c ($"{final} = select i1 {eqAny}, {(llTy ety)} {zero}, {(llTy ety)} {signed}")
        { Reg = final; Ty = ety }

/// Split a shape after `k` AXES, or refuse. A peel may only land on a GROUP
/// boundary: half of a compact group is not an array -- row i of a packed
/// triangle has extent `n - i`, which is not a static shape at all.
let private splitGroupsAt (k: int) (groups: Grp list) : Grp list * Grp list =
    let rec go acc taken rest =
        if taken = k then (List.rev acc, rest)
        elif taken > k then refuse "a peel that lands inside a compact index group"
        else
            match rest with
            | [] -> refuse "a peel of more axes than the array has"
            | g :: tl -> go (g :: acc) (taken + grpRank g) tl
    go [] 0 groups

/// Peel `k` leading indices, yielding the rank-(r-k) view they name. On a
/// pool this is one GEP -- rows of a row-major pool ARE contiguous sub-pools,
/// which is the whole reason the skeleton is not emitted. On a producer it is
/// index-list concatenation, with no materialization anywhere.
let private rowView (c: Ctx) (a: ArrVal) (idxs: Val list) : ArrVal =
    // Peeling ONE index off a ragged pair yields the row: a rank-1 view whose
    // extent is an OPERAND (offsets[i+1] - offsets[i]) rather than a static
    // number. On a pool that is one GEP, exactly like the dense case; on the
    // cell-congruent producer it is an ordinal rebase. The generic path below
    // must never see this shape -- half of a ragged group is not a static
    // shape, and splitGroupsAt says so.
    match a.Groups, idxs with
    | [ GCompact _ ], _ -> refuse "a partial index into a rank-1 compound (it has one axis)"
    | [ GRagged (_, table) ], [ i ] ->
        let off = raggedOffAt c table i.Reg
        let len = raggedLenAt c table i.Reg
        (match a.Src with
         | APool p -> { a with Groups = [ GDynDense len ]; Src = APool (gepCell c a.Elem p off) }
         | AVirtFlat f ->
             { a with Groups = [ GDynDense len ]
                      Src = AVirt (fun c2 tail ->
                                match tail with
                                | [ j ] -> f c2 { Reg = i64Add c2 off j.Reg; Ty = ScI64 }
                                | _ -> refuse "a ragged row read with more than one coordinate") }
         | AVirt _ -> refuse "a row view of a coordinate-bearing ragged producer")
    | _ ->
    let k = List.length idxs
    let (lead, rest) = splitGroupsAt k a.Groups
    match a.Src with
    | APool p ->
        // Offset in CELLS of the sub-pool: the leading indices addressed
        // against the full shape, times the size of what remains.
        let leadOff = storageOffset c lead idxs
        let block = shapeCells rest
        let off =
            if block = 1L then leadOff
            else
                let m = freshReg c
                ln c (renderBin { Dest = m; Opcode = "mul"; Flags = ""; Ty = ScI64; Lhs = leadOff; Rhs = string block })
                m
        { a with Groups = rest; Src = APool (gepCell c a.Elem p off) }
    | AVirt f -> { a with Groups = rest; Src = AVirt (fun c2 tail -> f c2 (idxs @ tail)) }
    | AVirtFlat f ->
        let flat = { a with Src = AVirt (fun c2 all -> f c2 { Reg = storageOffset c2 a.Groups all; Ty = ScI64 }) }
        { flat with Groups = rest; Src = AVirt (fun c2 tail ->
                        match flat.Src with
                        | AVirt g -> g c2 (idxs @ tail)
                        | _ -> refuse "a peeled flat producer") }

// ---------------------------------------------------------------------------
// The blocked-simplex nest (docs/plans/plan-simplex-blocked-compute.md)
//
// The canonical rank-2 domain {i <= j < n} (or {i < j < n}) is enumerated in
// one of two shapes, and both write EXACTLY the same set of cells:
//
//   SERIAL -- the plain triangle, cells in pool order. Row i runs
//   `n - i - strict` iterations: a trip count that changes every row, which
//   is what the vectorizer and any static schedule dislike.
//
//   BRICKED -- the simplex covered by BLOCKS of the B-wide tile grid. A block
//   whose two tiles differ is a DENSE B x B rectangle: i in tile t1 and j in
//   tile t2 > t1 gives i < j for free, so the canonicality constraint is
//   discharged by the block structure and costs no instruction. Only the T
//   on-diagonal blocks stay triangular, and they hold 1/T of the cells. Both
//   inner loops then have LITERAL trip counts, and the packed addressing
//   inside a brick is affine in the column (row base + j - i - strict, then
//   +1 per cell), which is the whole point.
//
// Block order is ascending-lex over the tile multiset -- (0,0), (0,1), ...,
// (0,T-1), (1,1), ... -- which is `SimplexBlocksCore.blockSequence`. Order is
// free for a map (distinct cells, independent writes) and is the DETERMINISM
// GUARANTEE for a fold, whose per-brick partials combine in exactly it.
// ---------------------------------------------------------------------------

/// `BLADE_LLVM_BRICKS`, read per call (never a module-level `let`, per the
/// repo's environment discipline). MEASUREMENT USE ONLY, exactly as plan
/// section 8.3 licenses: `off`/`0` forces the serial triangle, a number pins
/// the tile edge B, anything else defers to the derived policy.
type private BrickKnob =
    | BrickAuto
    | BrickOff
    | BrickFixed of int64

let private brickKnob () : BrickKnob =
    match System.Environment.GetEnvironmentVariable "BLADE_LLVM_BRICKS" with
    | null | "" -> BrickAuto
    | v ->
        match v.Trim().ToLowerInvariant() with
        | "off" | "0" | "none" -> BrickOff
        | "on" | "auto" | "1" -> BrickAuto
        | s ->
            match System.Int64.TryParse s with
            | true, b when b >= 1L -> BrickFixed b
            | _ -> BrickAuto

/// Bricks pay only when the traversal re-streams a row-operand working set
/// that cannot stay cached. Smallest measured winning set: 9.2 MB (1.38x
/// reassoc / 1.05x strict); 18.4 MB won 2.76x / 1.11x
/// (plan-simplex-blocked-compute.md section 0, third block). The threshold
/// sits just under the smallest measured win; below it the serial triangle
/// keeps the proven-fastest default. The principled form is "operand bytes
/// exceed the outermost cache"; 8 MiB is the measured stand-in until a cache
/// probe exists.
let [<Literal>] reuseThresholdBytes = 8388608L

/// The tile edge for a domain of extent `n`, or None for "run the serial
/// triangle". `licensed` is the fold-reordering licence: a MAP passes true
/// unconditionally (its cells are distinct and its writes independent -- plan
/// section 7, "brick order free"), a FOLD passes `foldReorderLicensed`,
/// because grouping the fold by brick reassociates it. `rowOpBytes` is the
/// construction-time reuse hint (ArrVal.RowOpBytes): every plain map carries
/// 0 and so never bricks by default -- the second-block verdict -- while a
/// row-operand producer at or above the threshold takes the divisor-preferred
/// reuse edge.
///
/// PUBLIC because it IS the gate. An unlicensed fold must never brick, and
/// that is a property of this function alone -- so `blade test llvm blocks`
/// asserts it here, by name and in both directions, rather than inferring it
/// from emitted text.
let brickTileEdge (licensed: bool) (rowOpBytes: int64) (n: int64) : int64 option =
    if not licensed then None
    else
        match brickKnob () with
        | BrickOff -> None
        | BrickFixed b -> if Blade.SimplexBlocksCore.tileCount n b >= 2L then Some b else None
        | BrickAuto ->
            if rowOpBytes >= reuseThresholdBytes then Blade.SimplexBlocksCore.reuseTileEdge n
            else Blade.SimplexBlocksCore.autoTileEdge n

/// One block of the decomposition, laid down as loops. `rowLo`/`colLo` are
/// operands (a tile loop's variable, or a literal for the peeled last tile);
/// `rows`/`cols` are LITERAL widths, which is what makes the trip counts
/// constants. `body` receives (i, p, offset) in STORAGE coordinates.
let private emitBrick (c: Ctx) (n: int64) (strict: bool)
                      (rowLo: string) (rows: int64) (colLo: string option) (cols: int64)
                      (body: Val -> Val -> Val -> unit) : unit =
    let s = if strict then 1L else 0L
    emitCountedLoop c rows (fun a ->
        let i = i64Add c rowLo a
        let rb = emitRowBase2 c n strict i
        match colLo with
        | None ->
            // ON-DIAGONAL block: columns start at the row itself, so the row
            // is a shrinking triangle of `cols - a - strict` cells and the
            // storage coordinate starts at 0. Serial by design (plan section
            // 8.2): these blocks are small, and covering them densely would
            // waste half of each and create write hazards on canonical cells.
            let len = i64Bin c "sub" (string (cols - s)) a
            emitCountedLoopTo c len (fun p ->
                body { Reg = i; Ty = ScI64 } { Reg = p; Ty = ScI64 }
                     { Reg = i64Add c rb p; Ty = ScI64 })
        | Some cl ->
            // OFF-DIAGONAL block: a dense rectangle, `cols` literal
            // iterations, every cell canonical by construction. The storage
            // coordinate and the pool offset are both affine in the column
            // counter, so each is one `add` past a per-row base.
            let pBase = i64Bin c "sub" cl (i64Add c i (string s))
            let oBase = i64Add c rb pBase
            emitCountedLoop c cols (fun b ->
                body { Reg = i; Ty = ScI64 }
                     { Reg = i64Add c pBase b; Ty = ScI64 }
                     { Reg = i64Add c oBase b; Ty = ScI64 }))

/// Enumerate a rank-2 compact group's canonical cells. `onBlock` wraps each
/// block's loops (the fold's per-brick partial hangs off it; a map passes
/// `fun emit -> emit ()`); `body` gets (i, p, pool offset).
let private emitSimplex2 (c: Ctx) (n: int64) (strict: bool) (tile: int64 option)
                         (onBlock: (unit -> unit) -> unit)
                         (body: Val -> Val -> Val -> unit) : unit =
    match tile with
    | None ->
        // The serial triangle IS one on-diagonal block covering everything.
        onBlock (fun () -> emitBrick c n strict "0" n None n body)
    | Some b ->
        let T = Blade.SimplexBlocksCore.tileCount n b
        let last = T - 1L
        let wLast = Blade.SimplexBlocksCore.tileWidth n b last
        // Row tiles 0 .. T-2 are FULL (width b); the last is peeled so every
        // emitted trip count stays a literal.
        emitCountedLoop c last (fun t1 ->
            let rowLo = i64Bin c "mul" t1 (string b)
            onBlock (fun () -> emitBrick c n strict rowLo b None b body)
            // Column tiles t1+1 .. T-2, also full.
            let cnt = i64Bin c "sub" (string (last - 1L)) t1
            emitCountedLoopTo c cnt (fun k ->
                let t2 = i64Bin c "add" (i64Bin c "add" t1 "1") k
                let colLo = i64Bin c "mul" t2 (string b)
                onBlock (fun () -> emitBrick c n strict rowLo b (Some colLo) b body))
            // ... then the ragged last column tile.
            onBlock (fun () -> emitBrick c n strict rowLo b (Some (string (last * b))) wLast body))
        // ... and finally the last diagonal block.
        onBlock (fun () -> emitBrick c n strict (string (last * b)) wLast None wLast body)

/// THE ARBITRARY-RANK SERIAL SIMPLEX: every canonical cell of a rank-r compact
/// group, once, in ascending-lex (= pool) order.
///
/// r nested counted loops, level k running its absolute coordinate from its
/// canonical floor (`0`, then `i_{k-1} + strict`) over the room left above it.
/// Nothing here is rank-2 shaped, and nothing is special-cased per rank: the
/// only rank-dependent quantities are the trip-count slack and the degree of
/// `emitPrefixTerm`'s polynomial, both derived from r.
///
/// THE COST ARGUMENT, which is why this is not merely "a triangular nest".
/// The pool offset is threaded down the nest as a running base: level k adds
/// its own term, which is invariant under every level inside it, so it is
/// emitted ONCE per iteration of level k rather than once per cell. The last
/// level's term degenerates to its own loop counter (`emitPrefixTerm`, m = 0),
/// so the innermost loop adds one `add` and walks the pool contiguously. Total
/// addressing cost is O(1) per cell at any rank, with no combinadic
/// arithmetic, no per-cell canonicality test, and no division except by the
/// compile-time factorials in the hoisted terms.
///
/// The trip count carries the strict slack (`n - lo - (r-1-k)` when strict):
/// the tuples it drops are exactly those with no room for the coordinates
/// still to place, so the nest never opens an inner loop it knows is empty.
let private emitSimplexSerialR (c: Ctx) (r: int) (n: int64) (strict: bool)
                               (body: Val list -> Val -> unit) : unit =
    let sInc = if strict then 1L else 0L
    let rec go (k: int) (lo: string) (baseOff: string) (accS: Val list) =
        if k = r then body (List.rev accS) { Reg = baseOff; Ty = ScI64 }
        else
            let room = n - (if strict then int64 (r - 1 - k) else 0L)
            let trips =
                match tryLitI64 lo with
                | Some l -> string (room - l)
                | None -> i64Bin c "sub" (string room) lo
            emitCountedLoopTo c trips (fun t ->
                // The loop counter IS the storage coordinate: it counts from
                // the canonical floor, which is what a packed coordinate means.
                let term = emitPrefixTerm c strict n r k lo t
                go (k + 1)
                   (i64Add c (i64Add c lo t) (string sInc))
                   (i64Add c baseOff term)
                   ({ Reg = t; Ty = ScI64 } :: accS))
    go 0 "0" "0" []

/// Enumerate a compact group's canonical cells at ANY rank, handing `body` the
/// group's STORAGE coordinates (one per level) and the pool offset.
///
/// `tile` selects the schedule: `None` is the serial simplex above (one block
/// covering the domain, so `onBlock` wraps it once); `Some B` is the blocked
/// decomposition, which is currently defined for rank 2 only -- the rank-r
/// block enumeration exists in `SimplexBlocksCore` (`blockSequence`,
/// `isDenseBrick`) but its emitter does not, and a fold that silently ran
/// serial when it asked to be blocked would corrupt the one thing the blocked
/// arm is for (a reassociated-but-deterministic combine order). So it refuses
/// by name instead.
let private emitSimplexR (c: Ctx) (r: int) (n: int64) (strict: bool) (tile: int64 option)
                         (onBlock: (unit -> unit) -> unit)
                         (body: Val list -> Val -> unit) : unit =
    match tile, r with
    | None, _ -> onBlock (fun () -> emitSimplexSerialR c r n strict body)
    | Some b, 2 -> emitSimplex2 c n strict (Some b) onBlock (fun i p off -> body [ i; p ] off)
    | Some _, _ ->
        refuse ($"a BLOCKED rank-{r} simplex (the blocked schedule is rank-2 only; the serial rank-{r} nest is supported)")

/// Enumerate a whole shape's STORAGE coordinates, innermost group last,
/// handing the body the coordinates and the pool offset. Dense groups are
/// plain counted loops; a compact group is its serial triangle. Bricking is
/// NOT applied here: it is a whole-array strategy, chosen by the caller for
/// the shapes where it is defined (a sole rank-2 simplex).
let private emitShapeNest (c: Ctx) (groups: Grp list) (body: Val list -> Val -> unit) : unit =
    let rec go (acc: Val list) (rest: Grp list) =
        match rest with
        | [] ->
            let idxs = List.rev acc
            body idxs { Reg = storageOffset c groups idxs; Ty = ScI64 }
        | GDense n :: tl -> emitCountedLoop c n (fun i -> go ({ Reg = i; Ty = ScI64 } :: acc) tl)
        | g :: tl when grpCompact g ->
            emitSimplexR c (grpRank g) (grpExtent g) (grpStrict g) None (fun emit -> emit ())
                (fun coords _ -> go (List.fold (fun a x -> x :: a) acc coords) tl)
        | GDomain plan :: [] when List.isEmpty acc ->
            // the sole group: its own counter IS the pool offset
            emitDomainNest c plan body
        | _ -> refuse "an index group the llvm lane cannot iterate"
    go [] groups

// ---------------------------------------------------------------------------
// Expression emission
// ---------------------------------------------------------------------------

/// Emit `e` and return its value, coerced to the type `IR.typeOf` reports for
/// the node. The coercion at the boundary is what keeps every consumer honest:
/// a node's emitted register always has the type the IR says it has, so no arm
/// below has to second-guess its children.
let rec private emitExpr (c: Ctx) (e: IRExpr) : Val =
    spineStatement c e |> ignore
    let v = emitRaw c e
    match scalarTyOf (typeOf e) with
    | Some t when t <> ScVoid && v.Ty <> ScVoid -> coerce c t v
    | _ -> v

and private emitRaw (c: Ctx) (e: IRExpr) : Val =
    match e with
    | IRLit (IRLitInt n) -> { Reg = string n; Ty = ScI64 }
    | IRLit (IRLitFloat f) -> { Reg = f64Const f; Ty = ScF64 }
    // The LLVM lane has no ScF32 (IRTScalar ETFloat32 maps to no scalar and
    // the gate declines Float32 programs), so a Float32 literal that somehow
    // arrives is widened -- value-preserving, and unreachable until an f32
    // scalar type exists here.
    | IRLit (IRLitFloat32 f) -> { Reg = f64Const (float f); Ty = ScF64 }
    | IRLit (IRLitBool b) -> { Reg = (if b then "true" else "false"); Ty = ScBool }
    | IRLit (IRLitString s) -> { Reg = stringGlobal c s; Ty = ScStr }
    | IRLit IRLitUnit -> { Reg = ""; Ty = ScVoid }

    // A runtime constraint guard (BL8001 value constraints, BL8006 checked
    // index conversions and subscripts, BL8010 the recursive-array budget):
    // statement-shaped and unit-valued, exactly the C++ lane's
    // `if (!(cond)) blade_rt::panic(code, message, file, line)`. The shim's
    // located panic renders the same `error[CODE]: MESSAGE` / `  --> file:line`
    // bytes, then the shadow-frame trace.
    | IRConstraintCheck (cond, code, message, span) ->
        let cv = emitExpr c cond
        if cv.Ty <> ScBool then refuse "a constraint check whose condition is not a Bool"
        let lPass = freshLbl c "check.pass"
        let lFail = freshLbl c "check.fail"
        ln c (renderBr { Cond = cv.Reg; IfTrue = lPass; IfFalse = lFail })
        lbl c lFail
        needShim c "blade_panic_at"
        let msg = stringGlobal c $"error[{code}]: {message}"
        ln c (renderCall { Dest = None; RetTy = ScVoid; Callee = "@blade_panic_at"
                           Args = [ ScStr, msg ] @ faultLocArgs c (SrcLoc span) })
        ln c "unreachable"
        lbl c lPass
        { Reg = ""; Ty = ScVoid }

    | IRVar (id, _) ->
        (match c.Slots.TryGetValue id with
         | true, (slot, sc) -> loadSlot c slot sc
         | _ ->
             if c.Callables.ContainsKey id then
                 refuse ($"function '{(c.Callables.[id]).Name}' used as a value -- the llvm lane emits direct calls only")
             else refuse ($"reference to an unbound variable (ir id {id})"))

    | IRParam (name, _, _) ->
        (match c.NameSlots.TryGetValue name with
         | true, (slot, sc) -> loadSlot c slot sc
         | _ -> refuse ($"reference to parameter '{name}' outside a function body"))

    | IRBinOp (mode, op, l, r, loc) ->
        if mode = IROuter then refuse "an outer-product binary operator ([+], [*])"
        emitBinOp c op l r loc

    | IRUnaryOp (op, x) -> emitUnary c op x

    // fma(a, b, c) -> llvm.fma.f64: the fused op is the semantics, so it is
    // emitted as the intrinsic (not fmul+fadd with `contract`), and it
    // carries NO fast-math flags -- correctly rounded on every lane.
    | IRFma (a, b, cc) ->
        need c "declare double @llvm.fma.f64(double, double, double)"
        let va = coerce c ScF64 (emitExpr c a)
        let vb = coerce c ScF64 (emitExpr c b)
        let vc = coerce c ScF64 (emitExpr c cc)
        let dest = freshReg c
        ln c (renderCall { Dest = Some dest; RetTy = ScF64; Callee = "@llvm.fma.f64"
                           Args = [ ScF64, va.Reg; ScF64, vb.Reg; ScF64, vc.Reg ] })
        { Reg = dest; Ty = ScF64 }

    | IRIf (cond, tb, fb) ->
        let resTy = requireScalar "an if-expression" (typeOf e)
        let cv = coerce c ScBool (emitExpr c cond)
        let slot = allocSlot c resTy
        let lThen = freshLbl c "if.then"
        let lElse = freshLbl c "if.else"
        let lEnd = freshLbl c "if.end"
        ln c (renderBr { Cond = cv.Reg; IfTrue = lThen; IfFalse = lElse })
        lbl c lThen
        storeSlot c slot resTy (emitExpr c tb)
        ln c (sprintf "br label %%%s" lEnd)
        lbl c lElse
        storeSlot c slot resTy (emitExpr c fb)
        ln c (sprintf "br label %%%s" lEnd)
        lbl c lEnd
        (match slot with
         | Some s -> loadSlot c s resTy
         | None -> { Reg = ""; Ty = ScVoid })

    | IRMatch (scrutinee, cases) -> emitMatch c e scrutinee cases

    | IRBeginPoolScope _ -> refuse "an inline pool scope must be let-bound"
    | IRReleasePools (IRVar (sid, _), pools, call) ->
        if c.PoolScopes.Count = 0 || c.PoolScopes.[c.PoolScopes.Count - 1].InlineId <> Some sid then
            refuse "an unmatched inline pool release"
        let scope = c.PoolScopes.[c.PoolScopes.Count - 1]
        for pool in pools do
            match pool with
            | IRVar (pid, _) ->
                match c.ArrSlots.TryGetValue pid with
                | true, { Src = APool p } when scope.Pools |> Seq.exists (fun (q, _) -> p = q) -> ()
                | _ -> refuse "an inline release does not own its named pool"
            | _ -> refuse "an inline release pool is not a binding reference"
        // BL7005 (the user's decision): this lane RETAINED an opt-in A7c kept
        // inline; once per region. A program the lane later refuses has its
        // warnings discarded by the channel reset of the C++ lane it falls back to.
        Blade.Optimize.noteReleaseRetained "llvm" pools call
        ln c ($"; A01: inline return cleanup: {call}")
        // These allocations were registered in this nested scope, never in
        // its parent. Removing the scope prevents a second free at exit.
        popPoolScope c None true
        { Reg = ""; Ty = ScVoid }
    | IRReleasePools _ -> refuse "an inline release has no scope reference"
    | IRLet (id, value, body) ->
        bindLetIn c id value body
        emitExpr c body

    | IRSequence exprs ->
        let mutable last = { Reg = ""; Ty = ScVoid }
        for x in exprs do last <- emitExpr c x
        last

    | IRAssign (target, value) ->
        (match target with
         // An element write through a `mut` array parameter. It aliases the
         // caller by construction: the parameter IS the caller's pool.
         | IRIndex (arrExpr, idxExprs, _) ->
             let a = materializeExpr c arrExpr
             let idxs = idxExprs |> List.map (fun ix -> coerce c ScI64 (emitExpr c ix))
             if List.length idxs <> a.Rank then
                 refuse "an element write through a partial index"
             // A write into COMPACT storage is refused, not canonicalized: a
             // store through (j, i) would silently land on the canonical cell
             // (i, j) and, for an antisymmetric group, would have to negate
             // the value to mean what the source said. That is a semantic
             // decision the surface has never had to make, so this lane will
             // not invent one.
             if a.Groups |> List.exists grpCompact then
                 refuse "an element write into compact (symmetric/antisymmetric) storage"
             (match a.Src with
              | APool p ->
                  let v = coerce c a.Elem (emitExpr c value)
                  storeCell c a.Elem (gepCell c a.Elem p (storageOffset c a.Groups idxs)) v
                  { Reg = ""; Ty = ScVoid }
              | _ -> refuse "an element write into a computed (non-stored) array")
         | IRVar (id, _) ->
             (match c.Slots.TryGetValue id with
              | true, (slot, sc) ->
                  let v = coerce c sc (emitExpr c value)
                  ln c ($"store {(llTy sc)} {v.Reg}, ptr {slot}")
                  { Reg = ""; Ty = ScVoid }
              | _ ->
                  if c.ArrSlots.ContainsKey id then
                      // Whole-array rebinding: the slot re-points, which is
                      // what the C++ lane's wrapper assignment does too. The
                      // rebound target may predate an open tracking scope, in
                      // which case the new pool escapes it (same rule as
                      // materializeExpr's upgrade).
                      // A function holds a PUBLISHED module array as a view
                      // loaded at its entry; re-pointing that view would
                      // rebind only the function's copy, so it refuses. In
                      // main the new pool is republished (materialized first:
                      // a function reads it through a bare pointer).
                      let published = c.PublishWanted.Contains id
                      if published && c.InFunction then
                          refuse "a function rebinding a whole module-level array"
                      let a = if published then materialize c (emitArr c value) else emitArr c value
                      bindArray c id "" a
                      c.DeferredLetValues.Remove id |> ignore
                      (match a.Src with
                       | APool p when c.PoolScopes |> Seq.exists (fun s -> s.KnownIds.Contains id) ->
                           keepPool c id p
                       | _ -> ())
                      if published then publishBinding c id
                      { Reg = ""; Ty = ScVoid }
                  else refuse "assignment to a variable with no storage in this scope")
         | _ -> refuse "assignment to a non-variable target")

    | IRApp (func, args, _) -> emitCall c func args

    // THE ONE ORDERED ITERATION CONSTRUCT the IR has, and the only place a
    // program's own sequencing survives into the back end. Two shapes lower
    // through it, both statement-shaped (unit-valued; everything they do they
    // do through stores): `let rec` -- with the front end's prefix reads
    // already turned into hoisted lag bindings and clamped, implicitly-zero
    // guarded reads -- and the rank-k dense fold `reduce(A, op, axes = k)`.
    | IRForRange (vid, lo, hi, body) -> emitForRange c vid lo hi body

    // The rec-array `while` guard's early exit: leave the innermost
    // IRForRange. As in the C++ lane (genBinding's IRBreakIf arm, which runs
    // peekAllocScopeFrees before its `break`), the pools this trip has
    // tracked SO FAR are freed first -- the bottom-of-trip frees never run on
    // this path. A slot loaded here holds this trip's pool or null (the
    // trip-entry resets dominate the break), so freeing it is exact. The
    // budget abort that rides with the guard is an ordinary
    // IRConstraintCheck after the loop.
    | IRBreakIf cond ->
        if c.BreakTargets.Count = 0 then
            refuse "a `while` guard outside the recursive array's loop"
        let (lExit, depth) = c.BreakTargets.Peek()
        if lExit.Value = "" then lExit.Value <- freshLbl c "range.exit"
        let cv = emitExpr c cond
        if cv.Ty <> ScBool then refuse "a `while` guard whose condition is not a Bool"
        let lBreak = freshLbl c "range.break"
        let lCont = freshLbl c "range.cont"
        ln c (renderBr { Cond = cv.Reg; IfTrue = lBreak; IfFalse = lCont })
        lbl c lBreak
        for k in c.PoolScopes.Count - 1 .. -1 .. depth do
            for (_, slot) in c.PoolScopes.[k].Pools do
                c.AnyFrees <- true
                needShim c "blade_free"
                let p = freshReg c
                ln c ($"{p} = load ptr, ptr {slot}")
                ln c ($"call void @blade_free(ptr {p})")
        ln c (sprintf "br label %%%s" lExit.Value)
        lbl c lCont
        { Reg = ""; Ty = ScVoid }

    // ---- array-derived scalars -------------------------------------------
    // A read CONSUMES its base where it can: one cell of a producer costs one
    // kernel application, and materializing a whole pool to fetch it was the
    // same shape the reduce/prodsum fixes removed. The two exceptions are the
    // same two (printing and sharing demand): a base with a SECOND reader
    // materializes so the readers share one pool, and a MODULE BINDING is
    // marked forced so it still prints (the C++ lane's
    // `forceDeferredPositionalReads` notes it for exactly this, corpus
    // loops/117, /192) without being stored here, where a pool allocated
    // inside a nest would not dominate the print that reads it afterwards.
    // A KEYED read of a domain array, `R((i, j))`: the coordinates are field
    // values; a pool reads at the lex rank (with the membership check inside
    // `domainRankReg`), a producer checks membership and runs the kernel.
    | IRIndex (arrExpr, [ IRTuple coords ], _)
        when (match arrayShapeOf (typeOf arrExpr) with
              | Some (_, [ GDomain _ ]) -> true
              | _ -> false) ->
        let a = consumeArrOperand c arrExpr
        let xs = coords |> List.map (fun x -> coerce c ScI64 (emitExpr c x))
        (match a.Groups, a.Src with
         | [ GDomain _ ], APool _ -> readCell c a xs
         | [ GDomain plan ], _ ->
             domainRankReg c plan xs |> ignore
             readCell c a xs
         | _ -> refuse "a keyed read of a non-domain array")
    | IRIndex (arrExpr, idxExprs, _) ->
        let a = consumeArrOperand c arrExpr
        // The KEYED read of a rank-1 compound, `S(k)` (the C++ lane's
        // compoundRead normalizes the scalar spelling to the 1-tuple read
        // `S((k))`; both spellings arrive here): the key is a SOURCE
        // position, resolved to its kept ordinal by binary search over the
        // kept-position table -- `compound_index_t<1>::linearize`'s exact
        // algorithm -- and a key that was not kept is BL8006 with that lane's
        // text and no location line (its throw carries none).
        match compactPosOf a, idxExprs with
        | Some (_, pos), _ when pos = compactNoPositions ->
            // Unreachable by the positions demand census (Optimize.planCompactionFusion):
            // a keyed read demands the table, so it is built. Never a lookup through null.
            failwith "BL9002 internal codegen invariant violated: a keyed read of a compound whose kept positions were elided (the positions demand census missed a consumer)"
        | Some (len, pos), ([ key ] | [ IRTuple [ key ] ]) ->
            let k = coerce c ScI64 (emitExpr c key)
            needShim c "blade_compact1_rank"
            let q = freshReg c
            ln c (renderCall { Dest = Some q; RetTy = ScI64; Callee = "@blade_compact1_rank"
                               Args = [ ScStr, pos; ScI64, len; ScI64, k.Reg ] })
            let missing = freshReg c
            ln c (renderCmp { Dest = missing; Kind = "icmp"; Pred = "slt"; Ty = ScI64; Lhs = q; Rhs = "0" })
            let lBad = freshLbl c "ckey.bad"
            let lOk = freshLbl c "ckey.ok"
            ln c (renderBr { Cond = missing; IfTrue = lBad; IfFalse = lOk })
            lbl c lBad
            needShim c "blade_panic_at"
            let m = stringGlobal c "error[BL8006]: index out of bounds: a key outside the sparse key set"
            ln c (renderCall { Dest = None; RetTy = ScVoid; Callee = "@blade_panic_at"
                               Args = [ ScStr, m; ScStr, "null"; ScI64, "0" ] })
            ln c "unreachable"
            lbl c lOk
            readCell c a [ { Reg = q; Ty = ScI64 } ]
        | Some _, _ -> refuse "a rank-1 compound read with other than one key"
        | None, _ ->
        let idxs = idxExprs |> List.map (fun ix -> coerce c ScI64 (emitExpr c ix))
        if List.length idxs <> a.Rank then
            refuse ($"a partial index ({(List.length idxs)} of {a.Rank}) in value position")
        // THE ONE PLACE ABSOLUTE COORDINATES ENTER. Everything else in this
        // file works in storage coordinates; a subscript the user wrote is
        // absolute and may name a mirror cell, so it canonicalizes here.
        canonRead c a idxs

    | IRExtent (arrExpr, dim) ->
        // extents() of a STATIC shape is a constant. The one runtime answer is
        // a peeled ragged row, whose length is the operand its view carries.
        (match arrayShapeOf (typeOf arrExpr) with
         | Some (_, groups) ->
             let extents = axisExtents groups
             if dim < 0 || dim >= List.length extents then
                 refuse ($"extents(_, {dim}) outside the array's rank")
             { Reg = string extents.[dim]; Ty = ScI64 }
         | None ->
             let a = emitArr c arrExpr
             (match a.Groups, dim with
              // A rank-1 compound's extent is its cardinality (the C++
              // lane's `.idx->cardinality`, the interpreter's Cardinality).
              | [ GDynDense len ], 0 | [ GCompact (len, _) ], 0 -> { Reg = len; Ty = ScI64 }
              | groups, d ->
                  let extents = axisExtents groups
                  if d < 0 || d >= List.length extents then
                      refuse ($"extents(_, {d}) outside the array's rank")
                  { Reg = string extents.[d]; Ty = ScI64 }))

    | IRRank arrExpr ->
        (match arrayShapeOf (typeOf arrExpr) with
         | Some (_, groups) -> { Reg = string (groups |> List.sumBy grpRank); Ty = ScI64 }
         | None ->
             let a = emitArr c arrExpr
             { Reg = string a.Rank; Ty = ScI64 })

    | IRReduce (arrExpr, kernelExpr, initExpr) -> emitReduce c arrExpr kernelExpr initExpr

    | IRReduceCompute (compExpr, kernelExpr, initExpr) ->
        (match emitReduceCompute c compExpr kernelExpr initExpr with
         | [ v ] -> v
         | vs -> refuse ($"a fused reduction producing {List.length vs} accumulators in scalar position"))

    | IRTupleProj (parent, idx, isFlat) ->
        (match tupleComponent c parent idx isFlat with
         | VScalar v -> v
         | _ -> refuse ($"tuple projection [{idx}] of a non-scalar component in value position"))

    | IRPure inner -> emitExpr c inner
    | IRCompute inner -> emitExpr c inner
    | IRBind (comp, cont) -> emitExpr c (bindContinuationBody c comp cont)

    // `a <|> b`: the left operand is evaluated EXACTLY ONCE and kept if it is
    // non-zero, else the right. The C++ lane spells it as a ternary over a
    // bound temporary; branches give the same one-evaluation guarantee here
    // and keep the right side unevaluated when the left wins.
    | IRChoice (a, b) ->
        let resTy = requireScalar "a choice expression" (typeOf e)
        let l = coerce c resTy (emitExpr c a)
        let nz = coerce c ScBool l
        let slot = allocSlot c resTy
        let lKeep = freshLbl c "choice.keep"
        let lElse = freshLbl c "choice.else"
        let lEnd = freshLbl c "choice.end"
        ln c (renderBr { Cond = nz.Reg; IfTrue = lKeep; IfFalse = lElse })
        lbl c lKeep
        storeSlot c slot resTy l
        ln c (sprintf "br label %%%s" lEnd)
        lbl c lElse
        storeSlot c slot resTy (emitExpr c b)
        ln c (sprintf "br label %%%s" lEnd)
        lbl c lEnd
        (match slot with Some s -> loadSlot c s resTy | None -> { Reg = ""; Ty = ScVoid })

    // `guard(p, body)` = `p ? body : <type-appropriate zero>`.
    | IRGuard (cond, body) ->
        let resTy = requireScalar "a guard expression" (typeOf e)
        let cv = coerce c ScBool (emitExpr c cond)
        let slot = allocSlot c resTy
        let lThen = freshLbl c "guard.then"
        let lElse = freshLbl c "guard.else"
        let lEnd = freshLbl c "guard.end"
        ln c (renderBr { Cond = cv.Reg; IfTrue = lThen; IfFalse = lElse })
        lbl c lThen
        storeSlot c slot resTy (emitExpr c body)
        ln c (sprintf "br label %%%s" lEnd)
        lbl c lElse
        storeSlot c slot resTy { Reg = (match resTy with ScF64 -> f64Const 0.0 | ScBool -> "false" | _ -> "0"); Ty = resTy }
        ln c (sprintf "br label %%%s" lEnd)
        lbl c lEnd
        (match slot with Some s -> loadSlot c s resTy | None -> { Reg = ""; Ty = ScVoid })

    | IRProdSum args -> emitProdSum c args

    // contains(B, x): the membership test, under THE ONE target-evaluation
    // contract of every lane (Optimize's membership-hoisting note; the
    // interpreter's Loops.fs order): the set operand first, then the target
    // exactly ONCE, before any cell is read -- an empty set still evaluates
    // it. While a host statement that emitMain built a structure for is being
    // emitted in main (MemberTables, membership hoisting), a probe of that
    // set reads the structure through the shared C core (blade_member.h via
    // the shim); anywhere else, the linear scan with early exit. Rank-1 sets
    // of Int64 / Float64 / Bool cells, stored (a pool) -- dense or a rank-1
    // compound's compact buffer; anything else refuses (the program falls
    // back to the C++ lane) rather than invent a semantics.
    | IRContains (arrExpr, valueExpr) ->
        let live =
            match arrExpr with
            | IRVar (sid, _) when not c.InFunction ->
                (match c.MemberTables.TryGetValue sid with
                 | true, slot -> Some slot
                 | _ -> None)
            | _ -> None
        match live with
        | Some slot ->
            // The set is a stored, never-assigned module binding (the plan's
            // admission): evaluating it reads a name and nothing else.
            let x = coerce c ScI64 (emitExpr c valueExpr)
            needShim c "blade_member_has_i64"
            let r = freshReg c
            ln c ($"{r} = call i32 @blade_member_has_i64(ptr {slot}, i64 {x.Reg})")
            let hit = freshReg c
            ln c ($"{hit} = icmp ne i32 {r}, 0")
            { Reg = hit; Ty = ScBool }
        | None ->
            let a = consumeArrOperand c arrExpr
            let n =
                match a.Groups with
                | [ GDense n ] -> string n
                | [ GDynDense len ] -> len
                | [ GCompact (len, _) ] -> len
                | _ -> refuse "contains over a set that is not rank-1 (dense, or a rank-1 compound)"
            (match a.Src with
             | APool _ -> ()
             | _ -> refuse "contains over a computed (unstored) set")
            (match a.Elem with
             | ScI64 | ScF64 | ScBool -> ()
             | _ -> refuse "contains over a set of String cells")
            let x = coerce c a.Elem (emitExpr c valueExpr)
            // BLOCKED early exit: 64 cells at a time, each block a branch-free
            // OR of the compares (a counted loop the vectorizer takes), then
            // the exit test. Reading the rest of a block after a hit reads a
            // stored pool in bounds and changes nothing: the boolean is the
            // scan's.
            let found = allocaOf c "i1"
            ln c ($"store i1 false, ptr {found}")
            let pos = allocaOf c "i64"
            ln c ($"store i64 0, ptr {pos}")
            let lCond = freshLbl c "has.cond"
            let lBlock = freshLbl c "has.block"
            let lEnd = freshLbl c "has.end"
            ln c (sprintf "br label %%%s" lCond)
            lbl c lCond
            let p = freshReg c
            ln c ($"{p} = load i64, ptr {pos}")
            let more = freshReg c
            ln c (renderCmp { Dest = more; Kind = "icmp"; Pred = "slt"; Ty = ScI64; Lhs = p; Rhs = n })
            let f = freshReg c
            ln c ($"{f} = load i1, ptr {found}")
            let nf = freshReg c
            ln c ($"{nf} = xor i1 {f}, true")
            let go = freshReg c
            ln c ($"{go} = and i1 {more}, {nf}")
            ln c (renderBr { Cond = go; IfTrue = lBlock; IfFalse = lEnd })
            lbl c lBlock
            let rem = i64Bin c "sub" n p
            let short = freshReg c
            ln c (renderCmp { Dest = short; Kind = "icmp"; Pred = "slt"; Ty = ScI64; Lhs = rem; Rhs = "64" })
            let len = freshReg c
            ln c ($"{len} = select i1 {short}, i64 {rem}, i64 64")
            emitCountedLoopTo c len (fun k ->
                let idx = i64Bin c "add" p k
                let cell = readCell c a [ { Reg = idx; Ty = ScI64 } ]
                let eq = freshReg c
                (match a.Elem with
                 // IEEE `==`, the C++ lane's and the interpreter's float
                 // equality: NaN matches nothing, -0.0 matches +0.0
                 | ScF64 -> ln c (renderCmp { Dest = eq; Kind = "fcmp"; Pred = "oeq"; Ty = ScF64; Lhs = cell.Reg; Rhs = x.Reg })
                 | ty -> ln c (renderCmp { Dest = eq; Kind = "icmp"; Pred = "eq"; Ty = ty; Lhs = cell.Reg; Rhs = x.Reg }))
                let acc = freshReg c
                ln c ($"{acc} = load i1, ptr {found}")
                let acc2 = freshReg c
                ln c ($"{acc2} = or i1 {acc}, {eq}")
                ln c ($"store i1 {acc2}, ptr {found}"))
            let p2 = i64Bin c "add" p len
            ln c ($"store i64 {p2}, ptr {pos}")
            ln c (sprintf "br label %%%s" lCond)
            lbl c lEnd
            let r = freshReg c
            ln c ($"{r} = load i1, ptr {found}")
            { Reg = r; Ty = ScBool }

    | other ->
        refuse ($"the IR node {(caseName other)} in value position -- no arm in the llvm lane")

/// `for k in lo..hi { ... }` -- a SEQUENTIAL counted loop, laid down by the
/// same `emitCountedLoopTo` every other loop in this file uses, with the body
/// emitted ONCE into the loop's own block.
///
/// NO FAST-MATH, licensed or not, and nothing to license: a recursive array is
/// ordered by construction (step n reads the prefix step n-1 wrote), and the
/// rank-k dense fold this also carries is the front end's own accumulation
/// nest, which folds in declared row-major order with one accumulator. The
/// arithmetic inside the body is born under whatever scope already applies --
/// `FoldFmf` is not opened here.
///
/// BOTH BOUNDS ARE EVALUATED ONCE, ahead of the loop. That is the
/// interpreter's contract verbatim (`Interp/Core.fs`: "IRForRange evaluates
/// both bounds once, then runs a single reused int64"), and it is the only
/// shape that keeps a bound expression's own instructions out of the trip
/// count. The C++ lane's `for (int64_t __k = lo; __k < hi; __k++)` agrees on
/// every bound a Blade program can currently present -- the desugars build
/// literals, or an extent that `StaticEval` already folded to one.
///
/// THE LOOP VARIABLE IS THE USER'S `Int64` and gets its own slot rather than
/// riding the counter register: the body reads it as an ordinary `IRVar`, and
/// the implicit-zero guards the recursive-array desugar emits compare it (and
/// its lags) against zero, so a counter and a loop variable are not
/// interchangeable the moment `lo` is not zero.
///
/// A NEGATIVE TRIP COUNT IS ZERO ITERATIONS -- `0 slt <negative>` is false at
/// the first test -- which is what `hi <= lo` means in both other lanes.
and private emitForRange (c: Ctx) (vid: IRId) (lo: IRExpr) (hi: IRExpr) (body: IRExpr) : Val =
    let loV = coerce c ScI64 (emitExpr c lo)
    let hiV = coerce c ScI64 (emitExpr c hi)
    // The literal-zero start folds away here exactly as `i64Add` folds its
    // identity: `for n in 0..N` is then the bare counted loop and the loop
    // variable IS the counter, with no arithmetic standing between them.
    let trips = if loV.Reg = "0" then hiV.Reg else i64Bin c "sub" hiV.Reg loV.Reg
    let slot = ensureSlot c vid ScI64
    forceLoopBodyProducers c body
    // Wave 3 (E-G5; Blade.Optimize.RecStepTable): the time loop of a threaded
    // `let rec` step. This lane has no thread runtime (G11): the sequential
    // loop IS the emission, and the record says so (the request was inlined
    // away with the step's final map, so this is its census).
    (match Blade.Optimize.RecStepTable.tryStep vid with
     | Some entry when Blade.Effects.Decisions.active () ->
        Blade.Effects.Decisions.record
            { Rule = "rec-step-parallel"; Version = 1; Span = c.DeclSpan
              Subject = entry.Subject
              Outcome = Blade.Effects.Declined $"llvm lane: the LLVM lane has no thread-level form; the `where omp` request of `{entry.KernelName}` (the step's final map) runs the recursion's steps serially"
              Evidence = [ "the sequential time loop" ] }
     | _ -> ())
    // B2r for a recursion (round-2 cell epilogues; Blade.Optimize.CellFoldTable):
    // this loop is a step's slice-write-fused CELL loop whose row the step
    // then folds with `prodsum(row, row)`. In a build that keeps the serial
    // association that prodsum is emitProdSum's ordered chain from +0, and
    // the same chain -- `fmul` of the cell by itself, `fadd` into the
    // accumulator, the same flags -- advances here once per trip on the
    // value the trip just stored; the fold let then loads the chain. The
    // licensed prodsum carries `reassoc` (the vectorizer's tree): declined.
    let cellChain =
        let declined (entry: Blade.Optimize.CellFoldEntry) (why: string) =
            if Blade.Effects.Decisions.active () then
                Blade.Effects.Decisions.record
                    { Rule = "cell-epilogue"; Version = 1; Span = c.DeclSpan
                      Subject = entry.Subject; Outcome = Blade.Effects.Declined ("llvm lane: " + why)
                      Evidence = [ "the row's prodsum runs on the finished row after the step" ] }
            None
        match Blade.Optimize.CellFoldTable.tryLoop vid with
        | Some entry when Blade.CodeGenState.fpReassocEnabled () ->
            declined entry (Blade.Optimize.cellEpilogueLicensedRowDecline "the finished row")
        | Some entry ->
            let rec lastStmt (e: IRExpr) =
                match e with
                | IRLet (_, _, (IRLet _ as rest)) -> lastStmt rest
                | IRLet (_, v, IRLit IRLitUnit) -> Some v
                | _ -> None
            (match lastStmt body with
             | Some (IRAssign (IRIndex (IRVar (b, ArrayElem at), [ _; IRVar (k2, _) ], _), IRVar (sv, svT)))
                when b = entry.BufferId && k2 = vid && sv = entry.StoredVar ->
                 if scalarTyOf at.ElemType <> Some ScF64 then declined entry "the trajectory is not Float64 (v1)"
                 else
                 let acc = allocaOf c "double"
                 ln c ($"store double {(f64Const 0.0)}, ptr {acc}")
                 Some (entry, acc, IRVar (sv, svT))
             | _ -> declined entry "the cell loop's shape changed after planning (its last statement is not the planned store)")
        | None -> None
    let before = arrSlotSnapshot c
    // The exit an IRBreakIf (`while` guard) leaves through. The label is
    // minted and emitted only when a break uses it, so an unguarded `let rec`
    // keeps its exact emission (register numbering included).
    let lExit = ref ""
    c.BreakTargets.Push((lExit, c.PoolScopes.Count))
    let trip (k: string) =
        ln c ($"store i64 {(i64Add c loV.Reg k)}, ptr {slot}")
        // Each trip is a tracking scope: a step's materializations (a rank-2
        // `let rec`'s per-step slice, a copy-on-alias) are dead when the trip
        // ends, and freeing them here is what keeps a long trajectory's
        // resident footprint at one step's temps instead of all of them.
        // Escapes (a pool upgrading a binding that predates the trip) null
        // their slot via `keepPool` and survive.
        pushPoolScope c
        emitExpr c body |> ignore
        (match cellChain with
         | Some (_, acc, stored) ->
            let x = coerce c ScF64 (emitExpr c stored)
            let prod = freshReg c
            ln c (renderBin { Dest = prod; Opcode = "fmul"; Flags = fmfFor c ScF64; Ty = ScF64; Lhs = x.Reg; Rhs = x.Reg })
            let accFlags = withFoldFmf c (Blade.CodeGenState.fpReassocEnabled ()) (fun () -> fmfFor c ScF64)
            let cur = loadSlot c acc ScF64
            let s = freshReg c
            ln c (renderBin { Dest = s; Opcode = "fadd"; Flags = accFlags; Ty = ScF64; Lhs = cur.Reg; Rhs = prod })
            ln c ($"store double {s}, ptr {acc}")
         | None -> ())
        popPoolScope c None true
    try emitCountedLoopTo c trips trip
    finally c.BreakTargets.Pop() |> ignore
    (match cellChain with
     | Some (entry, acc, _) ->
        entry.EmittedLlvm.Value <- Some acc
        if Blade.Effects.Decisions.active () then
            Blade.Effects.Decisions.record
                { Rule = "cell-epilogue"; Version = 1; Span = c.DeclSpan
                  Subject = entry.Subject
                  Outcome = Blade.Effects.Applied
                  Evidence = [ "llvm lane: row chain interleave: the step's prodsum(row, row) chain advances on each cell right behind its store, same ops and flags as the prodsum; the fold let loads the chain" ] }
     | None -> ())
    if lExit.Value <> "" then
        ln c (sprintf "br label %%%s" lExit.Value)
        lbl c lExit.Value
    requireArrSlotsStable c before
    { Reg = ""; Ty = ScVoid }

/// Materialize, BEFORE the loop opens, every deferred array binding the body
/// will read BY NAME.
///
/// This is `genForRangeBinding`'s own first act -- `forceDeferredPositionalReads`
/// (CodeGenBinding.fs), which the C++ lane runs for the obvious reason that a
/// producer materialized inside the body would re-run once per trip. Here it is
/// not an optimization at all but the difference between valid and invalid IR:
/// the pool would additionally be a register the code AFTER the loop does not
/// dominate, and the binding -- now forced, and therefore printed -- is read
/// exactly there.
///
/// WHICH READS COUNT AS BY-NAME is decided by `collectDeferredPositionalReads`,
/// the C++ lane's own collector, CALLED rather than re-walked. Not a
/// convenience: forcing one binding more than the other lane forces adds a
/// printed line, and the differential would report the extra line as drift; a
/// second copy of that walk would drift the moment either lane grew a node.
/// The probe context it takes is read for its deferred KEY SET only, so the
/// values are placeholders.
and private forceLoopBodyProducers (c: Ctx) (body: IRExpr) : unit =
    let deferred =
        [ for KeyValue (id, a) in c.ArrSlots do
            match a.Src with
            | APool _ -> ()
            | AVirt _ | AVirtFlat _ -> yield (id, IRLit IRLitUnit) ]
        |> Map.ofList
    if not (Map.isEmpty deferred) then
        let probe = { Blade.CodeGenState.emptyContext () with DeferredComputations = deferred }
        for id in Blade.CodeGenFusion.collectDeferredPositionalReads probe body do
            match c.ArrSlots.TryGetValue id, Map.tryFind id c.BindingValues with
            // A MODULE binding goes through materializeExpr, so in main it
            // hoists like every other forced module binding -- an inline
            // upgrade here would sit after a later hoist that reads it.
            | (true, _), Some v -> materializeExpr c (IRVar (id, typeOf v)) |> ignore
            | (true, a), None ->
                c.ArrSlots.[id] <- materialize c a
                markForced c id
            | _ -> ()

/// The array bindings a loop body may not REBIND, checked by snapshot.
///
/// Every array in this emitter is a register -- a pool pointer, or a producer
/// closure over one -- and a register born inside a loop body does not
/// dominate the code after the loop. So a binding that already existed when
/// the loop opened and holds something DIFFERENT when it closes is a dominance
/// violation waiting for its next reader: `let mut acc = A(0)` re-pointed
/// once per step is the leading-axis fold's shape, and the honest fix is a
/// pointer slot for array bindings (the same treatment scalars already get),
/// not a patch at this seam. Until that exists the program is refused by name
/// and the C++ lane takes it.
///
/// REFERENCE IDENTITY IS THE EXACT TEST, and it is exact rather than
/// conservative: `materialize` of an already-stored array returns the very
/// object it was handed, so a binding merely read (or forced, which re-stores
/// the same instance) is still the same instance, while every rebinding builds
/// a new record. Checking AFTER the body is emitted costs nothing -- a refusal
/// discards the whole module, so the instructions already written are never
/// seen.
and private arrSlotSnapshot (c: Ctx) : (IRId * ArrVal) list =
    [ for KeyValue (id, a) in c.ArrSlots -> (id, a) ]

and private requireArrSlotsStable (c: Ctx) (before: (IRId * ArrVal) list) : unit =
    for (id, a) in before do
        match c.ArrSlots.TryGetValue id with
        | true, now when obj.ReferenceEquals(now, a) -> ()
        | _ ->
            refuse ($"an array binding (ir id {id}) rebound inside a for-range loop -- its storage would not outlive the loop body")

/// `prodsum(x1..xk)` = `sum_t prod_l x_l(t)` over rank-1 operands of equal
/// extent: ONE loop, one accumulator, ascending -- the shape the moment
/// formers' fiber kernels land on, and the same association the C++ IIFE uses.
and private emitProdSum (c: Ctx) (args: IRExpr list) : Val =
    if List.isEmpty args then refuse "prodsum with no operands"
    // Operands are CONSUMED, on the same three-way rule `emitReduce` uses --
    // the loop below reads every operand's cell exactly once, so running a
    // producer costs precisely what filling a pool from it would, minus the
    // pool. This matters because prodsum's home is the moment-formers' fiber
    // kernels, i.e. inside a kernel body, where forcing meant one pool and
    // one fill pass PER OUTPUT CELL -- the same shape as the reduce leak.
    //
    // The two exceptions are the ones printing and sharing demand:
    //   * a name with a SECOND reader must materialize, so the readers share
    //     one pool rather than re-running the producer each time;
    //   * a MODULE BINDING must be marked forced so it still prints (the C++
    //     lane notes prodsum operands for exactly this, corpus loops/117),
    //     but must NOT be materialized here -- this prodsum may sit inside a
    //     loop nest, and a pool allocated there would not dominate the print
    //     that reads it afterwards.
    // (gap G60 (b)) prodsum reads EVERY cell and its arithmetic cannot fail,
    // so a sole-read module operand stays consumed in place when the program
    // has no assignment at all (consumeArrOperandAllTotal); otherwise it is
    // stored at its statement first.
    let ops =
        args
        |> List.map (consumeArrOperandAllTotal c)
    let head = List.head ops
    if head.Rank <> 1 then refuse "prodsum over a rank-2+ operand"
    let ty = if head.Elem = ScBool then ScI64 else head.Elem
    let acc = allocaOf c (llTy ty)
    // The OUTER accumulation is `+`, which is licensed by construction: the
    // fold kernel is not a user kernel at all here, it is the builtin the
    // C++ lane's own prodsum IIFE reassociates in the same builds
    // (`CodeGenExpr.fs`'s `if not (fpReassocEnabled ()) then serialForm`).
    // The per-element PRODUCT chain gets no such flag -- reordering the
    // factors of one element is a different reassociation nobody licensed.
    let accFlags =
        withFoldFmf c (Blade.CodeGenState.fpReassocEnabled ()) (fun () -> fmfFor c ty)
    ln c ($"""store {(llTy ty)} {(match ty with ScF64 -> f64Const 0.0 | _ -> "0")}, ptr {acc}""")
    emitCountedLoopTo c (soleExtentOp head) (fun t ->
        let iv = { Reg = t; Ty = ScI64 }
        let mutable prod = coerce c ty (readCell c head [ iv ])
        for o in List.tail ops do
            let v = coerce c ty (readCell c o [ iv ])
            let d = freshReg c
            ln c (renderBin { Dest = d; Opcode = (if ty = ScF64 then "fmul" else "mul"); Flags = fmfFor c ty; Ty = ty; Lhs = prod.Reg; Rhs = v.Reg })
            prod <- { Reg = d; Ty = ty }
        let cur = loadSlot c acc ty
        let s = freshReg c
        ln c (renderBin { Dest = s; Opcode = (if ty = ScF64 then "fadd" else "add"); Flags = accFlags; Ty = ty; Lhs = cur.Reg; Rhs = prod.Reg })
        ln c ($"store {(llTy ty)} {s}, ptr {acc}"))
    loadSlot c acc ty

/// `c >>= k`: bind the continuation's parameter to the computation and answer
/// its BODY, so the caller emits it in whatever position the bind sits in.
and private bindContinuationBody (c: Ctx) (comp: IRExpr) (cont: IRExpr) : IRExpr =
    let cl =
        match resolveCallable cont with
        | Some cl when cl.Params.Length = 1 -> cl
        | Some cl -> refuse ($"a bind continuation '{cl.Name}' of {cl.Params.Length} parameters")
        | None -> refuse "a bind whose continuation does not resolve to a callable"
    let p = List.head cl.Params
    (match classifyValue c comp with
     | VArray a -> bindKernelParam c p (KArray a)
     | VScalar v -> bindKernelParam c p (KScalar v)
     | _ -> refuse "a bind over a pack-valued computation")
    cl.Body

and private tupleComponent (c: Ctx) (parent: IRExpr) (idx: int) (isFlat: bool) : ValKind =
    let parts = emitTupleParts c parent
    let flatAt (k: int) =
        if k < 0 || k >= List.length parts then
            refuse ($"tuple projection [{k}] outside a pack of {List.length parts}")
        parts.[k]
    // A STRUCTURAL index (`t[i]`, destructuring) counts the parent TYPE's
    // components, while the pack is flat: for `((1, 2), 3)`, `t[1]` is the
    // third leaf, not the second, and `t[0]` is the two-leaf slice. Leaf
    // offsets come from the type; a pack whose leaf count disagrees with its
    // type refuses rather than guess.
    if isFlat then flatAt idx
    else
        match Blade.IR.stripUnits (typeOf parent) with
        | IRTTuple ts ->
            let rec leafCount (t: IRType) =
                match Blade.IR.stripUnits t with
                | IRTTuple sub -> sub |> List.sumBy leafCount
                | _ -> 1
            if List.sumBy leafCount ts <> List.length parts then
                refuse "a tuple pack whose leaf count disagrees with its type"
            if idx < 0 || idx >= List.length ts then
                refuse ($"tuple projection [{idx}] outside a {List.length ts}-component tuple")
            let start = ts |> List.take idx |> List.sumBy leafCount
            (match Blade.IR.stripUnits ts.[idx] with
             | IRTTuple _ -> VTuple (parts |> List.skip start |> List.take (leafCount ts.[idx]))
             | _ -> flatAt start)
        | _ ->
            // No tuple type to count with: structural and flat agree only when
            // the pack has no nested component, which a flat pack cannot show.
            if parts |> List.exists (function VTuple _ -> true | _ -> false) then
                refuse "a structural tuple projection over a pack with no tuple type"
            flatAt idx

/// The per-AXIS extents of an array-valued EXPRESSION -- what `extents()` and
/// `rank()` report. Prefers the declared type (free) and falls back to
/// emitting the array (not free, but correct for a shape the type erased).
and private arrayShapeOfExpr (c: Ctx) (e: IRExpr) : Sc * int64 list =
    match arrayShapeOf (typeOf e) with
    | Some (elem, groups) -> (elem, axisExtents groups)
    | None ->
        let a = emitArr c e
        (a.Elem, a.Extents)

/// A result slot, or None for a unit-valued construct that stores nothing.
and private allocSlot (c: Ctx) (ty: Sc) : string option =
    if ty = ScVoid then None
    else Some (allocaOf c (llTy ty))

and private storeSlot (c: Ctx) (slot: string option) (ty: Sc) (v: Val) : unit =
    match slot with
    | Some s ->
        let v = coerce c ty v
        ln c ($"store {(llTy ty)} {v.Reg}, ptr {s}")
    | None -> ()

and private loadSlot (c: Ctx) (slot: string) (ty: Sc) : Val =
    let dest = freshReg c
    ln c ($"{dest} = load {(llTy ty)}, ptr {slot}")
    { Reg = dest; Ty = ty }

/// The two shim arguments that locate a fault for its panic's `--> file:line`:
/// the source file as a string constant (or null) and the line, exactly the
/// pair the C++ lane's panicSpanArgs renders.
and private faultLocArgs (c: Ctx) (loc: SrcLoc) : (Sc * string) list =
    let span = loc.Span
    match span.File with
    | Some f when f <> "" -> [ ScStr, stringGlobal c f; ScI64, (if span.StartLine > 0 then string span.StartLine else "0") ]
    | _ -> [ ScStr, "null"; ScI64, (if span.StartLine > 0 then string span.StartLine else "0") ]

/// A runtime GUARD panic's location: the declaration being emitted
/// (`Ctx.DeclSpan`), the twin of the C++ lane's `declPanicArgs`.
and private declLocArgs (c: Ctx) : (Sc * string) list =
    faultLocArgs c (SrcLoc c.DeclSpan)

/// Emit an INLINED NAMED FUNCTION's body (a tuple-returning call, a capturing
/// callee) as the declaration being emitted: the C++ lane CALLS that
/// function, whose guard panics (BL8002 / BL8003 / BL8006: `declLocArgs`) are
/// located at its own declaration (CodeGenState.emittedDeclSpan: its span,
/// else its source's; none for a block-local function), so this lane locates
/// them there too -- exactly as emitFunctionBody does for an outlined body --
/// instead of at the caller's binding, which a destructuring
/// `let (a, b) = f(..)` does not even have (gap G76). Every guard emitted
/// while the body is walked follows, planned ones included (they read
/// `declLocArgs` too). Decision records made inside take the function as
/// their span and subject, as an outlined body's do. Kernels (lambdas) are
/// NOT routed here: the C++ lane locates a lambda's guard at its enclosing
/// declaration, which is what `DeclSpan` already holds.
and private withCalleeDecl<'T> (c: Ctx) (cl: IRCallable) (body: unit -> 'T) : 'T =
    let savedSpan = c.DeclSpan
    let savedName = c.DeclName
    c.DeclSpan <-
        (let s = Blade.IR.declSpanOf cl.Name
         if s.StartLine > 0 then s else Blade.IR.declSpanOf cl.SourceName)
    c.DeclName <- (if String.IsNullOrEmpty cl.SourceName then cl.Name else cl.SourceName)
    try body ()
    finally
        c.DeclSpan <- savedSpan
        c.DeclName <- savedName

and private emitBinOp (c: Ctx) (op: IRBinOp) (l: IRExpr) (r: IRExpr) (loc: SrcLoc) : Val =
    match op with
    // `&&` / `||` SHORT-CIRCUIT, and that is not a micro-optimization here:
    // a plain `and i1` would evaluate a right operand C++ never runs, and the
    // guarded-divisor idiom (`d != 0 && n / d > 1`) turns an unreached sdiv
    // into immediate UB.
    | IRAnd | IROr -> emitShortCircuit c op l r
    | IRCaret ->
        // The arithmetic contract (docs/formalism.md section 2.4), the twin
        // of CodeGenExprSupport.renderContractBinOp: integer ^ integer is
        // EXACT and wraps (it used to go through a double `pow`, so 3^39 came
        // back rounded); a negative exponent panics BL8013 in the shim. Real
        // `^` is blade_fpow -- x * x at an exponent of exactly 2, else libm pow.
        let a0 = emitExpr c l
        let b0 = emitExpr c r
        if promote a0.Ty b0.Ty = ScI64 then
            let a = coerce c ScI64 a0
            let b = coerce c ScI64 b0
            match literalIntOf r with
            | Some n when n >= 0L && n <= 8L ->
                // A small literal exponent unrolls to a multiply chain: the
                // same residue modulo 2^64 as the shim's square-and-multiply.
                if n = 0L then { Reg = "1"; Ty = ScI64 }
                else
                    let mutable acc = a
                    for _ in 2L .. n do
                        let dest = freshReg c
                        ln c (renderBin { Dest = dest; Opcode = "mul"; Flags = ""; Ty = ScI64; Lhs = acc.Reg; Rhs = a.Reg })
                        acc <- { Reg = dest; Ty = ScI64 }
                    acc
            | _ -> callShim c "blade_ipow" ScI64 ([ ScI64, a.Reg; ScI64, b.Reg ] @ faultLocArgs c loc)
        elif a0.Ty = ScF64 && b0.Ty = ScI64 then
            // Float ^ Int: the exponent is NOT converted (formalism 2.4) --
            // repeated squaring, blade_fpowi, the twin of blade_arith::fpowi.
            // Not llvm.powi (unspecified multiply order, an i32 exponent, and
            // lowered to libm pow on this target). A literal 2 is the same
            // single product the loop computes (1.0 * (x * x)).
            match r with
            | IRLit (IRLitInt 2L) ->
                let dest = freshReg c
                ln c (renderBin { Dest = dest; Opcode = "fmul"; Flags = fmfFor c ScF64; Ty = ScF64; Lhs = a0.Reg; Rhs = a0.Reg })
                { Reg = dest; Ty = ScF64 }
            | _ -> callShim c "blade_fpowi" ScF64 [ ScF64, a0.Reg; ScI64, b0.Reg ]
        else
            let a = coerce c ScF64 a0
            let b = coerce c ScF64 b0
            match r with
            | IRLit (IRLitInt 2L) | IRLit (IRLitFloat 2.0) ->
                let dest = freshReg c
                ln c (renderBin { Dest = dest; Opcode = "fmul"; Flags = fmfFor c ScF64; Ty = ScF64; Lhs = a.Reg; Rhs = a.Reg })
                { Reg = dest; Ty = ScF64 }
            | _ -> callShim c "blade_fpow" ScF64 [ ScF64, a.Reg; ScF64, b.Reg ]
    | IRMath2 "log_base" ->
        // No libm log_base; the quotient IS the definition, matching
        // CodeGenExprSupport.renderMath2.
        let a = callLibm1 c "log" (coerce c ScF64 (emitExpr c l))
        let b = callLibm1 c "log" (coerce c ScF64 (emitExpr c r))
        let dest = freshReg c
        ln c (renderBin { Dest = dest; Opcode = "fdiv"; Flags = fmfFor c ScF64; Ty = ScF64; Lhs = a.Reg; Rhs = b.Reg })
        { Reg = dest; Ty = ScF64 }
    | IRMath2 "atan2" ->
        let a = coerce c ScF64 (emitExpr c l)
        let b = coerce c ScF64 (emitExpr c r)
        callLibm2 c "atan2" a b
    | IRMath2 name -> refuse ($"the binary math intrinsic '{name}'")
    | _ ->
        let a0 = emitExpr c l
        // BIND ONCE, the C++ lane's M2 rule (CodeGenExpr.isBindOnceCompactRead,
        // shared): the same canonicalizing compact read on both sides
        // (`t(i, j, k) * t(i, j, k)`) is one network, one offset and one load
        // -- explicitly, not left to GVN. Pure read of one cell: no value or
        // order changes.
        let b0 = if l = r && Blade.CodeGenExpr.isBindOnceCompactRead typeOf l then a0 else emitExpr c r
        let common = promote a0.Ty b0.Ty
        let a = coerce c common a0
        let b = coerce c common b0
        let dest = freshReg c
        match op with
        | IREq | IRNeq | IRLt | IRLe | IRGt | IRGe ->
            let kind = if common = ScF64 then "fcmp" else "icmp"
            let pred =
                match common, op with
                // ORDERED comparisons except `!=`: C's `a != b` is `!(a == b)`,
                // which is true when either operand is NaN -- `une`, not `one`.
                | ScF64, IREq -> "oeq" | ScF64, IRNeq -> "une"
                | ScF64, IRLt -> "olt" | ScF64, IRLe -> "ole"
                | ScF64, IRGt -> "ogt" | ScF64, IRGe -> "oge"
                | _, IREq -> "eq" | _, IRNeq -> "ne"
                | _, IRLt -> "slt" | _, IRLe -> "sle"
                | _, IRGt -> "sgt" | _, IRGe -> "sge"
                | _ -> refuse "an unrecognized comparison"
            ln c (renderCmp { Dest = dest; Kind = kind; Pred = pred; Ty = common; Lhs = a.Reg; Rhs = b.Reg })
            { Reg = dest; Ty = ScBool }
        | _ ->
            let opcode =
                match common, op with
                | ScF64, IRAdd -> "fadd" | ScF64, IRSub -> "fsub"
                | ScF64, IRMul -> "fmul" | ScF64, IRDiv -> "fdiv"
                | ScF64, IRMod -> refuse "`%` applied to floating-point operands"
                | ScI64, IRAdd -> "add" | ScI64, IRSub -> "sub"
                | ScI64, IRMul -> "mul"
                // C++ integer division and `%` truncate toward zero: sdiv/srem.
                | ScI64, IRDiv -> "sdiv" | ScI64, IRMod -> "srem"
                | _ -> refuse ($"arithmetic on {(llTy common)} operands")
            match common, op, literalIntOf r with
            // A zero or -1 divisor (or one not known) goes through the shim:
            // zero panics BL8013 and MIN / -1 wraps, where sdiv/srem are UB
            // (the arithmetic contract; blade_rt::idiv in the C++ lane). A
            // nonzero literal other than -1 can do neither: plain sdiv/srem.
            | ScI64, (IRDiv | IRMod), lit when (match lit with Some n -> n = 0L || n = -1L | None -> true) ->
                callShim c (if op = IRDiv then "blade_idiv" else "blade_imod") ScI64 ([ ScI64, a.Reg; ScI64, b.Reg ] @ faultLocArgs c loc)
            | _ ->
                ln c (renderBin { Dest = dest; Opcode = opcode; Flags = fmfFor c common; Ty = common; Lhs = a.Reg; Rhs = b.Reg })
                { Reg = dest; Ty = common }

and private emitShortCircuit (c: Ctx) (op: IRBinOp) (l: IRExpr) (r: IRExpr) : Val =
    let slot = allocaOf c "i1"
    let a = coerce c ScBool (emitExpr c l)
    ln c ($"store i1 {a.Reg}, ptr {slot}")
    let lRhs = freshLbl c "sc.rhs"
    let lEnd = freshLbl c "sc.end"
    // `&&` evaluates the right side only when the left held; `||` only when it
    // did not.
    if op = IRAnd then ln c (renderBr { Cond = a.Reg; IfTrue = lRhs; IfFalse = lEnd })
    else ln c (renderBr { Cond = a.Reg; IfTrue = lEnd; IfFalse = lRhs })
    lbl c lRhs
    let b = coerce c ScBool (emitExpr c r)
    ln c ($"store i1 {b.Reg}, ptr {slot}")
    ln c (sprintf "br label %%%s" lEnd)
    lbl c lEnd
    loadSlot c slot ScBool

and private emitUnary (c: Ctx) (op: IRUnaryOp) (x: IRExpr) : Val =
    match op with
    | IRNeg ->
        let v = emitExpr c x
        let dest = freshReg c
        (match v.Ty with
         | ScF64 ->
             ln c ($"{dest} = fneg{(fmfFor c ScF64)} double {v.Reg}")
             { Reg = dest; Ty = ScF64 }
         | ScI64 ->
             ln c (renderBin { Dest = dest; Opcode = "sub"; Flags = ""; Ty = ScI64; Lhs = "0"; Rhs = v.Reg })
             { Reg = dest; Ty = ScI64 }
         | other -> refuse ($"negation of a {(llTy other)} value"))
    | IRNot ->
        let v = coerce c ScBool (emitExpr c x)
        let dest = freshReg c
        ln c (renderBin { Dest = dest; Opcode = "xor"; Flags = ""; Ty = ScBool; Lhs = v.Reg; Rhs = "true" })
        { Reg = dest; Ty = ScBool }
    // On a REAL operand conj and real are the identity and imag is zero,
    // exactly as <complex>'s arithmetic overloads define them. A complex
    // operand never reaches here: its type refuses first.
    | IRConj | IRReal -> emitExpr c x
    | IRImag -> { Reg = f64Const 0.0; Ty = ScF64 }
    | IRArg -> refuse "arg() (complex phase)"
    | IRMath name ->
        (match Map.tryFind name libmUnary with
         | Some fn -> callLibm1 c fn (coerce c ScF64 (emitExpr c x))
         | None when name = "lgamma" || name = "digamma" ->
             refuse ($"{name} -- Blade's own series lives in blade_runtime.hpp and has no libm twin")
         | None -> refuse ($"the math intrinsic '{name}'"))
    // Explicit numeric cast: the lane's own scalar coercion (sitofp/fptosi)
    // covers the Float64/Int64 targets; the widths this lane has no scalar
    // for (Float32/Int32/complex) refuse like every other Float32/complex
    // program does.
    | IRCast (ETFloat64, _) ->
        let v = emitExpr c x
        if v.Ty = ScI64 && Blade.CodeGenExprSupport.i64SplitInScope () then emitI64ToF64 c v else coerce c ScF64 v
    // Float -> Int64 is the checked conversion of the arithmetic contract:
    // NaN / out of range panics BL8014 (blade_f2i64), where fptosi is poison.
    | IRCast (ETInt64, loc) ->
        let v = emitExpr c x
        if v.Ty = ScF64 then callShim c "blade_f2i64" ScI64 ([ ScF64, v.Reg ] @ faultLocArgs c loc)
        else coerce c ScI64 v
    | IRCast (et, _) -> refuse ($"a numeric cast to {(Blade.Types.castNameOf et)}")

/// `Float64(x)` of an Int64: the exact split conversion, the twin of the C++
/// lane's blade_arith::i64_to_f64 (blade_runtime.hpp, which carries the
/// exactness argument). A plain `sitofp i64` has no AVX2 packed form, so a
/// vectorized loop holding one SCALARIZES it -- per 4 lanes four extracts, four
/// vcvtsi2sd and the re-inserts, which was most of blade-llvm's band cell loop
/// (bench gap G33). Here the high word converts through `sitofp i32` and the
/// low word through the 2^52 magic exponent, both packed. Only `contract` may
/// ride the arithmetic (an fma of the last two steps rounds the same exact
/// value once); `reassoc` would regroup it around the magic constant, so a
/// fold kernel's FoldFmf is deliberately NOT applied.
/// Taken only in a closed-form domain's cell code (CodeGenExprSupport's split
/// scope, the C++ lane's rule): elsewhere the plain `sitofp` -- the same
/// double, one instruction where the split is nine in scalar code.
and private emitI64ToF64 (c: Ctx) (v: Val) : Val =
    let fl = if factEnabled factFmf && contractLicensed () then " contract" else ""
    let step (rhs: string) =
        let d = freshReg c
        ln c ($"{d} = {rhs}")
        d
    let hi = step ($"lshr i64 {v.Reg}, 32")
    let hi32 = step ($"trunc i64 {hi} to i32")
    let hiD = step ($"sitofp i32 {hi32} to double")
    let lo = step ($"and i64 {v.Reg}, 4294967295")
    let loBits = step ($"or i64 {lo}, 4841369599423283200")
    let loMagic = step ($"bitcast i64 {loBits} to double")
    let loD = step ($"fsub{fl} double {loMagic}, {(f64Const 4503599627370496.0)}")
    let hiScaled = step ($"fmul{fl} double {hiD}, {(f64Const 4294967296.0)}")
    { Reg = step ($"fadd{fl} double {hiScaled}, {loD}"); Ty = ScF64 }

and private callLibm1 (c: Ctx) (fn: string) (arg: Val) : Val =
    need c ($"declare double @{fn}(double{(paramAttr ())}){(attrRef c grpExternReturns)}")
    let dest = freshReg c
    ln c (renderCall { Dest = Some dest; RetTy = ScF64; Callee = "@" + fn; Args = [ ScF64, arg.Reg ] }
          + (if libmNoFold.Contains fn then " nobuiltin" else ""))
    { Reg = dest; Ty = ScF64 }

and private callLibm2 (c: Ctx) (fn: string) (a: Val) (b: Val) : Val =
    need c ($"declare double @{fn}(double{(paramAttr ())}, double{(paramAttr ())}){(attrRef c grpExternReturns)}")
    let dest = freshReg c
    ln c (renderCall { Dest = Some dest; RetTy = ScF64; Callee = "@" + fn; Args = [ ScF64, a.Reg; ScF64, b.Reg ] }
          + (if libmNoFold.Contains fn then " nobuiltin" else ""))
    { Reg = dest; Ty = ScF64 }

and private emitMatch (c: Ctx) (whole: IRExpr) (scrutinee: IRExpr) (cases: IRMatchCase list) : Val =
    if List.isEmpty cases then refuse "a match with no arms"
    let resTy = requireScalar "a match expression" (typeOf whole)
    // The scrutinee as a tree: a scalar is one leaf; a tuple's flat pack is
    // regrouped by its type (IRTTuple nests where the pack does not).
    let tree =
        match Blade.IR.stripUnits (typeOf scrutinee) with
        | IRTTuple _ as tupTy ->
            let parts =
                emitTupleParts c scrutinee
                |> List.map (function
                    | VScalar v -> v
                    | _ -> refuse "a match over a tuple with a non-scalar component")
            let rec build (t: IRType) (rest: Val list) : MatchTree * Val list =
                match Blade.IR.stripUnits t, rest with
                | IRTTuple ts, _ ->
                    let (kids, rest') =
                        ts |> List.fold (fun (acc, r) ct ->
                            let (k, r') = build ct r
                            (k :: acc, r')) ([], rest)
                    (MNode (List.rev kids), rest')
                | _, v :: rest' -> (MLeaf v, rest')
                | _, [] -> refuse "a tuple scrutinee with fewer components than its type"
            match build tupTy parts with
            | (t, []) -> t
            | _ -> refuse "a tuple scrutinee with more components than its type"
        | _ -> MLeaf (emitExpr c scrutinee)
    let rec leaves (t: MatchTree) : Val list =
        match t with
        | MLeaf v -> [ v ]
        | MNode ks -> ks |> List.collect leaves
    let slot = allocSlot c resTy
    let lEnd = freshLbl c "match.end"
    let lFail = freshLbl c "match.fail"
    let rec arms (cs: IRMatchCase list) =
        match cs with
        | [] -> ln c (sprintf "br label %%%s" lFail)
        | case :: rest ->
            let lNext = freshLbl c "match.next"
            // Pattern test. A wildcard/variable arm has none -- it always
            // matches -- so `lNext` for that arm is simply unreachable, which
            // is well-formed as long as it still gets a terminator (it does,
            // from the arm that follows or from the empty-case arm above).
            // The pattern test, structurally: a literal leaf that differs
            // jumps to `lNext`; a variable binds a leaf as a scalar and a
            // subtree as a flat pack; `h :: t` peels the first component and
            // matches the REST -- the bare element for a pair (Blade has no
            // 1-tuple), the interpreter's tryMatch rule.
            let rec test (pat: IRPattern) (t: MatchTree) : unit =
                match pat, t with
                | IRPatWild, _ -> ()
                | IRPatVar vid, MLeaf sv ->
                    let slotV = allocaOf c (llTy sv.Ty)
                    ln c ($"store {(llTy sv.Ty)} {sv.Reg}, ptr {slotV}")
                    c.Slots.[vid] <- (slotV, sv.Ty)
                | IRPatVar vid, MNode _ ->
                    c.TupleSlots.[vid] <- leaves t |> List.map VScalar
                | IRPatLit lit, MLeaf sv ->
                    let lv = coerce c sv.Ty (emitExpr c (IRLit lit))
                    if sv.Ty = ScStr then refuse "a String literal pattern"
                    let r = freshReg c
                    let kind = if sv.Ty = ScF64 then "fcmp" else "icmp"
                    let pred = if sv.Ty = ScF64 then "oeq" else "eq"
                    ln c (renderCmp { Dest = r; Kind = kind; Pred = pred; Ty = sv.Ty; Lhs = sv.Reg; Rhs = lv.Reg })
                    let lBody = freshLbl c "match.body"
                    ln c (renderBr { Cond = r; IfTrue = lBody; IfFalse = lNext })
                    lbl c lBody
                | IRPatTuple pats, MNode ks when List.length pats = List.length ks ->
                    List.iter2 test pats ks
                | IRPatCons (hp, tp), MNode (k0 :: (_ :: _ as more)) ->
                    test hp k0
                    test tp (match more with [ one ] -> one | _ -> MNode more)
                | other, _ ->
                    refuse (sprintf "the match pattern %A -- the llvm lane handles wildcard, variable, literal, tuple and cons patterns" other)
            test case.Pattern tree
            // A failed guard falls through to the next arm, and on the LAST
            // arm that lands in the panic block -- the same non-exhaustive
            // behavior renderMatchExpr gives the C++ lane.
            (match case.Guard with
             | Some g ->
                 let gv = coerce c ScBool (emitExpr c g)
                 let lGuarded = freshLbl c "match.guarded"
                 ln c (renderBr { Cond = gv.Reg; IfTrue = lGuarded; IfFalse = lNext })
                 lbl c lGuarded
             | None -> ())
            storeSlot c slot resTy (emitExpr c case.Body)
            ln c (sprintf "br label %%%s" lEnd)
            lbl c lNext
            arms rest
    arms cases
    lbl c lFail
    // Located at the declaration being emitted, as the C++ lane's
    // renderMatchExpr guard is (P3-31).
    needShim c "blade_panic_at"
    let msg = stringGlobal c "error[BL8002]: Blade: non-exhaustive match"
    ln c (renderCall { Dest = None; RetTy = ScVoid; Callee = "@blade_panic_at"; Args = [ ScStr, msg ] @ declLocArgs c })
    ln c "unreachable"
    lbl c lEnd
    match slot with
    | Some s -> loadSlot c s resTy
    | None -> { Reg = ""; Ty = ScVoid }

and private emitCall (c: Ctx) (func: IRExpr) (args: IRExpr list) : Val =
    let callable =
        match func with
        | IRVar (id, _) ->
            (match Map.tryFind id c.Callables with
             | Some cl -> cl
             | None -> refuse "a call to a function the module does not define (indirect or higher-order)")
        | _ -> refuse "an indirect / higher-order call"
    if callable.IsArityPoly then refuse ($"a call to the arity-polymorphic function '{callable.SourceName}'")
    if callable.IsCudaKernel || callable.IsMpiParallel then
        refuse ($"a call to '{callable.SourceName}', which carries a cuda/mpi strategy")
    // ARGUMENT ORDER (gap G57; Codex second look of W4-1, finding 5). A call
    // holding an explicit `|> compute` argument evaluates EVERY argument left
    // to right (formalism 10.2). A BARE deferred array argument would not
    // keep that order on the ordinary path: in an out-of-line call
    // materializeExpr hoists its producer to the statement's start
    // (dominance), and in an inlined capturing call it stays a producer the
    // body runs later. Such a call therefore takes the ORDERED path (the C++
    // lane's genOrderedCallBinding): each argument is evaluated at its source
    // position -- in main as its own positional statement (the hoist point
    // moves to the argument's start, so a force inside argument k lands
    // after arguments 0 .. k-1 and still dominates the rest of main), in a
    // function body in place (no hoisting there) -- and a bare deferred array
    // argument is FORCED there under its own name (materializeExpr: it is
    // stored, and at main's top level it prints, as in the C++ lane), for the
    // out-of-line and the capturing-inline call alike. The path needs the
    // call to be a statement's whole value -- main's top-level ROOT (as the
    // C++ lane's genOrderedCallBinding), any function-body statement
    // (Ctx.SpineCallArgs) -- since nested in an expression a
    // positional statement would not dominate; anything else is refused,
    // fail closed. (The C++ lane orders only a binding whose root is the
    // call, too: the nested mixed call is OPEN under G57 / G79.)
    // A name is a still-deferred array when its slot is a producer, or when it
    // has no slot yet and its module value is a deferred form (a deferred map
    // may be bound lazily, at its first use).
    let isBareDeferredArg (a: IRExpr) =
        match a with
        | IRVar (id, ty) when (match Blade.IR.stripUnits ty with ArrayElem _ -> true | _ -> false) ->
            (match c.ArrSlots.TryGetValue id with
             | true, { Src = APool _ } -> false
             | true, _ -> true
             | _ ->
                 match Map.tryFind id c.BindingValues with
                 | Some v -> not (match v with IRCompute _ | IRArrayLit _ -> true | _ -> false)
                 | None -> false)
        | _ -> false
    let inertArg (a: IRExpr) =
        match a with
        | IRLit _ | IRParam _ -> true
        | IRVar _ -> not (isBareDeferredArg a)
        | _ -> false
    let ordered =
        if args |> List.exists (function IRCompute _ -> true | _ -> false) then
            let indexed = List.indexed args
            let hazard =
                indexed |> List.exists (fun (j, a) ->
                    isBareDeferredArg a
                    && indexed |> List.exists (fun (i, b) -> i <> j && not (inertArg b)))
            if hazard && not (c.SpineCallArgs.Contains(box args)) then
                refuse ($"a call to '{callable.SourceName}' whose bare deferred array argument would be evaluated out of argument order (the ordered argument path needs the call to be a statement's whole value; the nested mixed call is OPEN in the C++ lane too -- gaps G57 / G79)")
            hazard
        else false
    // The bare deferred arguments, judged BEFORE any argument runs (forcing
    // one upgrades its slot).
    let bareDeferred = args |> List.map (fun a -> ordered && isBareDeferredArg a)
    // On the spine, argument k runs as its own positional statement.
    let atArgument () =
        if ordered && c.StmtStart >= 0 then positionalStatement c
    let afterArguments () = atArgument ()
    // An `omp` clause is a LICENCE to parallelize, not a demand: emitting the
    // serial body answers the same values. (Licensed reassociation is a
    // separate question and stays off -- see the numeric policy above.)
    //
    // A CAPTURING callee has no closure ABI in this lane, so it is INLINED
    // instead: capture references are ordinary IR ids and resolve through the
    // caller's own slot tables. Recursion would not terminate that way, so a
    // re-entered inline refuses rather than looping.
    if not (List.isEmpty callable.Captures) then
        if not (c.Inlining.Add callable.Id) then
            refuse ($"a recursive call to '{callable.SourceName}', which captures enclosing bindings")
        try
            let kargs =
                List.zip3 callable.Params args bareDeferred
                |> List.map (fun (p, a, bare) ->
                    atArgument ()
                    // On the ordered path an array argument that is still a
                    // PRODUCER here (an inline map, an array op) is evaluated
                    // at its position too -- it has no storage, so storing it
                    // now changes only WHEN it runs (a view, which a `mut`
                    // parameter must keep aliasing, is left as it is).
                    let orderedArr () =
                        if bare then materializeExpr c a
                        else
                            let av = emitArr c a
                            match a, av.Src with
                            | (IRIndex _ | IRVar _ | IRParam _ | IRLit _), _ -> av
                            | _, (AVirt _ | AVirtFlat _) when ordered -> materialize c av
                            | _ -> av
                    match arrayShapeOf p.Type with
                    | Some _ -> KArray (orderedArr ())
                    | None when (raggedArrShape p.Type).IsSome -> KArray (orderedArr ())
                    | None -> KScalar (emitExpr c a))
            afterArguments ()
            // the arguments are the caller's; the BODY is located at the
            // function's own declaration (gap G76, withCalleeDecl)
            withCalleeDecl c callable (fun () -> applyKernel c callable kargs)
        finally c.Inlining.Remove callable.Id |> ignore
    else
    if List.length callable.Params <> List.length args then
        refuse ($"a partial application of '{callable.SourceName}' ({(List.length args)} of {(List.length callable.Params)} arguments)")
    // Everything the callee can read from module scope must already sit in
    // its global. From main this is also the INITIALIZATION-ORDER proof: a
    // binding not yet bound has not been published either.
    (match c.ModuleReads.TryGetValue callable.Id with
     | true, reads ->
         for rid in reads do
             if not (c.Published.ContainsKey rid) then
                 let nm = defaultArg (Map.tryFind rid c.ModuleNames) (string rid)
                 // A DEFERRED MODULE MAP the callee evaluates privately (gap
                 // G75; its inputs are reads of their own, checked here too).
                 // From main that is the binding's meaning only while main has
                 // not forced it: once forced, its class value is the stored
                 // first-forcing snapshot (formalism 10.2), which the private
                 // evaluation would not read -- refused, fail closed. A call
                 // from a function body inherits its root call's admission.
                 if c.DeferredModule.ContainsKey rid then
                     if not c.InFunction then
                         match c.ArrSlots.TryGetValue rid with
                         | true, { Src = APool _ } ->
                             refuse ($"'{callable.SourceName}' reads the deferred module binding '{nm}', which main forced before this call (a function body evaluates a deferred module map privately; the forced snapshot is not published)")
                         | _ -> ()
                 else
                 refuse ($"'{callable.SourceName}' reads the module binding '{nm}', which is not a stored scalar or statically shaped array at this call")
     | _ -> ())
    let sym = ensureFunction c callable
    // An array argument crosses as its POOL POINTER, which is also what makes
    // `mut` work: the callee's element writes land in the caller's storage
    // because there is only one pool. Materializing here is therefore not an
    // optimization -- a producer has no address for the callee to write to.
    let argVals =
        List.zip callable.Params args
        |> List.collect (fun (p, a) ->
            // the ordered path (G57): argument k is its own positional
            // statement in main; materializeExpr then forces a bare deferred
            // argument at its own position, under its own name
            atArgument ()
            match arrayShapeOf p.Type with
            | Some (elem, groups) ->
                let av = materializeExpr c a
                if av.Elem <> elem || av.Groups <> groups then
                    refuse ($"argument shape disagrees with parameter '{p.Name}' of '{callable.SourceName}'")
                (match av.Src with
                 | APool ptr -> [ (ScStr, ptr) ]
                 | _ -> refuse "an unmaterialized array argument")
            | None ->
                match raggedArrShape p.Type with
                | Some (elem, rows) ->
                    // A ragged pair crosses as TWO arguments: the pool and its
                    // offsets table. A static table's global symbol IS a ptr
                    // operand; a forwarded parameter's table is a register.
                    let av = materializeExpr c a
                    (match av.Groups, av.Src with
                     | [ GRagged (r2, table) ], APool ptr when av.Elem = elem && r2 = string rows ->
                         let tp = match table with RtStatic (_, sym) -> sym | RtDynamic reg -> reg
                         [ (ScStr, ptr); (ScStr, tp) ]
                     | _ ->
                         refuse ($"argument shape disagrees with ragged parameter '{p.Name}' of '{callable.SourceName}'"))
                | None ->
                    let sc = requireScalar ($"parameter '{p.Name}' of '{callable.SourceName}'") p.Type
                    let v = coerce c sc (emitExpr c a)
                    [ (sc, v.Reg) ])
    afterArguments ()
    match arrayShapeOf callable.RetType with
    | Some _ ->
        let dest = freshReg c
        ln c (renderCall { Dest = Some dest; RetTy = ScStr; Callee = sym; Args = argVals })
        // A FreshPool callee's return is a pool THIS frame owns: track it
        // like a local allocation, so the enclosing scope's exit frees it.
        // This was the arena model's last unbounded edge -- an array-returning
        // function called per cell (or per `let rec` step) leaked its result
        // once per call. NotFresh stays untracked: unproven is leaked, never
        // freed.
        if c.PoolScopes.Count > 0 && c.Facts.FreshReturns.Contains callable.Id then
            let slot = allocaOf c "ptr"
            ln c ($"store ptr {dest}, ptr {slot}")
            c.PoolScopes.[c.PoolScopes.Count - 1].Pools.Add(dest, slot)
        { Reg = dest; Ty = ScStr }
    | None ->
    let retTy = requireScalar ($"the return type of '{callable.SourceName}'") callable.RetType
    if retTy = ScVoid then
        ln c (renderCall { Dest = None; RetTy = ScVoid; Callee = sym; Args = argVals })
        { Reg = ""; Ty = ScVoid }
    else
        let dest = freshReg c
        ln c (renderCall { Dest = Some dest; RetTy = retTy; Callee = sym; Args = argVals })
        { Reg = dest; Ty = retTy }

/// Claim a symbol for a callable and queue its body. The symbol is recorded
/// BEFORE the body is emitted, which is exactly what makes self-recursion work
/// and what makes a re-entrant reference cheap.
and private ensureFunction (c: Ctx) (cl: IRCallable) : string =
    match c.Emitted.TryGetValue cl.Id with
    | true, sym -> sym
    | _ ->
        let sym = $"@bl_{(sanitizeSym cl.Name)}_{cl.Id}"
        c.Emitted.[cl.Id] <- sym
        c.Pending.Enqueue cl
        sym

// ---------------------------------------------------------------------------
// Binding a name to a value
// ---------------------------------------------------------------------------

/// One slot per IR id, reused across every store to it. Reuse is what makes a
/// kernel parameter legal inside a loop: the slot is allocated once in the
/// entry block and rewritten each iteration.
and private ensureSlot (c: Ctx) (varId: IRId) (ty: Sc) : string =
    match c.Slots.TryGetValue varId with
    | true, (slot, existing) when existing = ty -> slot
    | _ ->
        let slot = allocaOf c (llTy ty)
        c.Slots.[varId] <- (slot, ty)
        slot

and private bindScalar (c: Ctx) (varId: IRId) (name: string) (v: Val) : unit =
    let slot = ensureSlot c varId v.Ty
    ln c ($"store {(llTy v.Ty)} {v.Reg}, ptr {slot}")
    if name <> "" then c.NameSlots.[name] <- (slot, v.Ty)

and private bindArray (c: Ctx) (varId: IRId) (name: string) (a: ArrVal) : unit =
    c.ArrSlots.[varId] <- a
    if name <> "" then c.NameArrSlots.[name] <- a

/// Emit one group_keys binding: the CSR build (counts, exclusive prefix sum,
/// scatter) in pure `.ll`, mirroring genGroupKeysBinding's static regimes
/// pass for pass. POSITIONAL (Idx<N> keys): bucket IS the key value, a
/// negative key drops its row from every pass, and a key at or past N aborts
/// BL8006. ENUM (int EnumIdx): bucket is the value's position in the declared
/// list via a select chain, a key outside the list aborts BL8006, and nothing
/// drops. DYNAMIC: first-occurrence discovery by a linear scan. Multi-key
/// tuple hashing and string keys are refused by name.
///
/// The four pools are ordinary `blade_alloc_cells` allocations, so the
/// existing scope machinery owns their lifetime. They arrive UNINITIALIZED
/// (blade_alloc_cells stopped zeroing in 4761eca, matching the C++ lane's
/// `new T[]`), so counts/fill/offsets[0] are zeroed explicitly below.
and private emitGroupKeysBinding (c: Ctx) (id: IRId) (keys: IRExpr list) (gkTy: IRType) : unit =
    let (outerIdx, enumValues) =
        match gkTy with
        | IRTGroupKeys (o, _, ev) -> (o, ev)
        | _ -> refuse "a group_keys binding without its IRTGroupKeys type"
    let ngroupsOpt = Blade.IRPrint.tryEvalIntIR outerIdx.Extent
    let keyArr =
        match keys with
        | [ k ] ->
            consumeArrOperand c k
        | _ -> refuse "multi-key group_keys (compound tuple hashing is not in the llvm lane yet)"
    if keyArr.Elem = ScStr then
        refuse "string group_keys (string EnumIdx dispatch is not in the llvm lane yet)"
    let nsrc =
        match keyArr.Groups with
        | [ GDense n ] -> n
        | _ -> refuse "group_keys over a key array that is not a dense rank-1 axis"
    let enumInts =
        enumValues
        |> Option.map (List.map (function
            | EVInt n -> n
            | EVString _ -> refuse "string EnumIdx group_keys (not in the llvm lane yet)"))
    let i64At (pool: string) (idx: string) : string = gepCell c ScI64 pool idx
    let ifThen (cond: string) (body: unit -> unit) =
        let lT = freshLbl c "gk.then"
        let lE = freshLbl c "gk.end"
        ln c (renderBr { Cond = cond; IfTrue = lT; IfFalse = lE })
        lbl c lT
        body ()
        ln c (sprintf "br label %%%s" lE)
        lbl c lE
    let readKey (ki: string) : string =
        (coerce c ScI64 (readCell c keyArr [ { Reg = ki; Ty = ScI64 } ])).Reg
    // The three regimes, as (group count operand, bucket-of-key emitter,
    // negative-keys-drop?). ENUM: position-in-list select chain, an unknown
    // key aborts BL8006, nothing drops. POSITIONAL: bucket IS the key,
    // negatives drop, a key >= N aborts BL8006. DYNAMIC: buckets in
    // first-occurrence order over the observed keys -- discovered through the
    // C core's key -> group map (one find-or-insert per row per pass; the
    // linear distinct-key scan it replaced was O(n*G)), negatives drop.
    // The C++ lane's BL8006 guards (genGroupKeysBinding; interpreter twin
    // ArrayOps.buildGroupKeys): a key outside its EnumIdx, or a positional key
    // past the last bucket, aborts -- located at the declaration being
    // emitted, as there -- instead of landing in bucket 0 or writing past the
    // counts pool.
    let panicUnless (ok: string) (msg: string) =
        let lOk = freshLbl c "gk.ok"
        let lBad = freshLbl c "gk.bad"
        ln c (renderBr { Cond = ok; IfTrue = lOk; IfFalse = lBad })
        lbl c lBad
        needShim c "blade_panic_at"
        let m = stringGlobal c ("error[BL8006]: " + msg)
        ln c (renderCall { Dest = None; RetTy = ScVoid; Callee = "@blade_panic_at"
                           Args = [ ScStr, m ] @ declLocArgs c })
        ln c "unreachable"
        lbl c lOk
    // AGGREGATE-ONLY GROUPING (Blade.Optimize.planGroupAggregate): the one
    // pass that also advances every peel's state, when this lane can place
    // it; otherwise the CSR below, emitted exactly as before (and the
    // handshake flag stays false, so the group_by and peel statements take
    // their ordinary forms).
    let panicCodeUnless (code: string) (ok: string) (msg: string) =
        let lOk = freshLbl c "gk.ok"
        let lBad = freshLbl c "gk.bad"
        ln c (renderBr { Cond = ok; IfTrue = lOk; IfFalse = lBad })
        lbl c lBad
        needShim c "blade_panic_at"
        let m = stringGlobal c ($"error[{code}]: " + msg)
        ln c (renderCall { Dest = None; RetTy = ScVoid; Callee = "@blade_panic_at"
                           Args = [ ScStr, m ] @ declLocArgs c })
        ln c "unreachable"
        lbl c lOk
    let gaPlan = Blade.Optimize.GroupAggTable.tryGk id
    (match gaPlan with Some p -> p.EmittedLlvm.Value <- false | None -> ())
    let gaPlaced =
        match gaPlan with
        | Some plan -> emitGroupAggPass c id plan keyArr nsrc gkTy readKey ifThen panicCodeUnless
        | None -> false
    if gaPlaced then () else
    // The dynamic regime's key -> group map (blade_member.h, through the
    // shim), freed after the scatter pass.
    let mutable dynMap : string option = None
    let (ngOp, bucketOf, dropNeg) : string * (string -> string) * bool =
        match enumInts with
        | Some vals ->
            let n =
                match ngroupsOpt with
                | Some n -> n
                | None -> refuse "an EnumIdx grouping without a static group count"
            let bucket (kReg: string) =
                let mutable acc = "0"
                let mutable found = "false"
                vals |> List.iteri (fun i v ->
                    let eq = freshReg c
                    ln c (renderCmp { Dest = eq; Kind = "icmp"; Pred = "eq"; Ty = ScI64; Lhs = kReg; Rhs = string v })
                    let sel = freshReg c
                    ln c (sprintf "%s = select i1 %s, i64 %d, i64 %s" sel eq i acc)
                    acc <- sel
                    let f = freshReg c
                    ln c (sprintf "%s = or i1 %s, %s" f found eq)
                    found <- f)
                panicUnless found "index out of bounds: a group_keys key outside its EnumIdx"
                acc
            (string n, bucket, false)
        | None ->
            match ngroupsOpt with
            | Some n ->
                // Only reached past the negative-key drop, so `k < n` is the
                // whole bound (the C++ lane's `(size_t)__k >= ngroups`).
                let bucket (kReg: string) =
                    let ok = freshReg c
                    ln c (renderCmp { Dest = ok; Kind = "icmp"; Pred = "slt"; Ty = ScI64; Lhs = kReg; Rhs = string n })
                    panicUnless ok ($"index out of bounds: a group_keys key outside 0 .. {n - 1L}")
                    kReg
                (string n, bucket, true)
            | None when (match keyArr.Src with APool _ -> true | _ -> false) ->
                // ONLY OVER A STORED KEY POOL (Codex second look at e6eadfa2,
                // BLOCKER): the passes below read the key column once each, and
                // a pool reads the same cells every time. A key PRODUCER (an
                // inline map left unforced, its kernel possibly writing) is
                // evaluated once per pass -- pre-existing -- and could answer
                // keys the discovery never saw; a find-or-insert would then
                // number groups past the CSR it already sized. Such a source
                // keeps the linear scan below, whose unknown key lands in
                // bucket 0 (bounded writes), exactly as before.
                // First-occurrence discovery through the C core's key ->
                // group map (blade_member.h; W3-1 retired the O(n * G) linear
                // scan of the distinct keys): the discovery pass inserts each
                // admitted key (numbering groups 0, 1, .. by first
                // occurrence), the count and scatter passes look the same key
                // up again (an existing key answers its number; the map's
                // table order is never read).
                let slot = allocaOf c "[128 x i8], align 8"
                dynMap <- Some slot
                needShim c "blade_gkmap_init_llvm"
                let okInit = freshReg c
                ln c ($"{okInit} = call i32 @blade_gkmap_init_llvm(ptr {slot}, i64 1)")
                let okB = freshReg c
                ln c ($"{okB} = icmp ne i32 {okInit}, 0")
                panicCodeUnless "BL8005" okB "group_keys: the key -> group map could not be allocated"
                let groupOf (kReg: string) =
                    needShim c "blade_gkmap_group_llvm"
                    let g = freshReg c
                    ln c ($"{g} = call i64 @blade_gkmap_group_llvm(ptr {slot}, i64 {kReg})")
                    let ok = freshReg c
                    ln c (renderCmp { Dest = ok; Kind = "icmp"; Pred = "sge"; Ty = ScI64; Lhs = g; Rhs = "0" })
                    panicCodeUnless "BL8005" ok "group_keys: the key -> group map could not grow"
                    g
                emitCountedLoop c nsrc (fun ki ->
                    let k = readKey ki
                    let ok = freshReg c
                    ln c (renderCmp { Dest = ok; Kind = "icmp"; Pred = "sge"; Ty = ScI64; Lhs = k; Rhs = "0" })
                    ifThen ok (fun () -> groupOf k |> ignore))
                needShim c "blade_gkmap_count_llvm"
                let ngReg = freshReg c
                ln c ($"{ngReg} = call i64 @blade_gkmap_count_llvm(ptr {slot})")
                (ngReg, groupOf, true)
            | None ->
                // First-occurrence discovery. distinct[] can hold at most
                // nsrc keys; the running count lives in a slot so the scan
                // loop's bound is the value discovered SO FAR.
                let distinct = allocPool c ScI64 (string nsrc)
                let ngSlot = allocaOf c "i64"
                ln c (sprintf "store i64 0, ptr %s" ngSlot)
                emitCountedLoop c nsrc (fun ki ->
                    let k = readKey ki
                    let ok = freshReg c
                    ln c (renderCmp { Dest = ok; Kind = "icmp"; Pred = "sge"; Ty = ScI64; Lhs = k; Rhs = "0" })
                    ifThen ok (fun () ->
                        let ng = freshReg c
                        ln c (sprintf "%s = load i64, ptr %s" ng ngSlot)
                        let foundSlot = allocaOf c "i1"
                        ln c (sprintf "store i1 false, ptr %s" foundSlot)
                        emitCountedLoopTo c ng (fun d ->
                            let dk = loadCell c ScI64 (i64At distinct d)
                            let eq = freshReg c
                            ln c (renderCmp { Dest = eq; Kind = "icmp"; Pred = "eq"; Ty = ScI64; Lhs = dk.Reg; Rhs = k })
                            let f = freshReg c
                            ln c (sprintf "%s = load i1, ptr %s" f foundSlot)
                            let nf = freshReg c
                            ln c (sprintf "%s = or i1 %s, %s" nf f eq)
                            ln c (sprintf "store i1 %s, ptr %s" nf foundSlot))
                        let f = freshReg c
                        ln c (sprintf "%s = load i1, ptr %s" f foundSlot)
                        let fresh = freshReg c
                        ln c (sprintf "%s = xor i1 %s, true" fresh f)
                        ifThen fresh (fun () ->
                            storeCell c ScI64 (i64At distinct ng) { Reg = k; Ty = ScI64 }
                            ln c (sprintf "store i64 %s, ptr %s" (i64Bin c "add" ng "1") ngSlot))))
                let ngReg = freshReg c
                ln c (sprintf "%s = load i64, ptr %s" ngReg ngSlot)
                let bucket (kReg: string) =
                    let bSlot = allocaOf c "i64"
                    ln c (sprintf "store i64 0, ptr %s" bSlot)
                    emitCountedLoopTo c ngReg (fun d ->
                        let dk = loadCell c ScI64 (i64At distinct d)
                        let eq = freshReg c
                        ln c (renderCmp { Dest = eq; Kind = "icmp"; Pred = "eq"; Ty = ScI64; Lhs = dk.Reg; Rhs = kReg })
                        let cur = freshReg c
                        ln c (sprintf "%s = load i64, ptr %s" cur bSlot)
                        let sel = freshReg c
                        ln c (sprintf "%s = select i1 %s, i64 %s, i64 %s" sel eq d cur)
                        ln c (sprintf "store i64 %s, ptr %s" sel bSlot))
                    let b = freshReg c
                    ln c (sprintf "%s = load i64, ptr %s" b bSlot)
                    b
                (ngReg, bucket, true)
    let ngPlus1 =
        match tryLitI64 ngOp with
        | Some n -> string (n + 1L)
        | None -> i64Bin c "add" ngOp "1"
    let counts = allocPool c ScI64 ngOp
    let offs = allocPool c ScI64 ngPlus1
    let fill = allocPool c ScI64 ngOp
    let perm = allocPool c ScI64 (string nsrc)
    emitCountedLoopTo c ngOp (fun gi ->
        storeCell c ScI64 (i64At counts gi) { Reg = "0"; Ty = ScI64 }
        storeCell c ScI64 (i64At fill gi) { Reg = "0"; Ty = ScI64 })
    storeCell c ScI64 (i64At offs "0") { Reg = "0"; Ty = ScI64 }
    /// One pass over the key array: `body ki bucket` under the regime's own
    /// admission rule.
    let withEachKey (body: string -> string -> unit) =
        emitCountedLoop c nsrc (fun ki ->
            let k = readKey ki
            if dropNeg then
                let ok = freshReg c
                ln c (renderCmp { Dest = ok; Kind = "icmp"; Pred = "sge"; Ty = ScI64; Lhs = k; Rhs = "0" })
                ifThen ok (fun () -> body ki (bucketOf k))
            else body ki (bucketOf k))
    // Pass 1: counts[bucket]++ (counts zeroed above).
    withEachKey (fun _ b ->
        let p = i64At counts b
        let cur = loadCell c ScI64 p
        storeCell c ScI64 p { Reg = i64Bin c "add" cur.Reg "1"; Ty = ScI64 })
    // Exclusive prefix sum from offsets[0] = 0 (stored above).
    emitCountedLoopTo c ngOp (fun gi ->
        let o = loadCell c ScI64 (i64At offs gi)
        let n = loadCell c ScI64 (i64At counts gi)
        storeCell c ScI64 (i64At offs (i64Bin c "add" gi "1"))
            { Reg = i64Bin c "add" o.Reg n.Reg; Ty = ScI64 })
    // Pass 2: scatter row ordinals group-contiguously.
    withEachKey (fun ki b ->
        let o = loadCell c ScI64 (i64At offs b)
        let fp = i64At fill b
        let f = loadCell c ScI64 fp
        storeCell c ScI64 (i64At perm (i64Bin c "add" o.Reg f.Reg)) { Reg = ki; Ty = ScI64 }
        storeCell c ScI64 fp { Reg = i64Bin c "add" f.Reg "1"; Ty = ScI64 })
    (match dynMap with
     | Some slot ->
         needShim c "blade_gkmap_fini_llvm"
         ln c ($"call void @blade_gkmap_fini_llvm(ptr {slot})")
     | None -> ())
    c.GroupKeys.[id] <- { NGroups = ngOp; Offsets = offs; Perm = perm; NSrc = nsrc; Agg = None }

/// The AGGREGATE-ONLY grouping's one pass (Blade.Optimize.planGroupAggregate,
/// gap G71; the C++ lane's genGroupAggKeysBinding twin): per row in source
/// order, the key (the negative-key drop and the BL8006 guards exactly as the
/// CSR's first key pass), the group (positional: the key; EnumIdx: the
/// declared-order select chain; dynamic: ONE find-or-insert in
/// blade_member.h's key -> group map through the shim, first-occurrence
/// numbering, each group a record of 8-byte slots -- slot 0 the count), then
/// each state's step: UNSEEDED -- the first value stored (no kernel call),
/// every later one `acc = k(acc, v)` through `applyKernel` (the fold's own
/// inlined kernel, no fold flags: the strict chain); SEEDED -- `acc = k(acc,
/// v)` from the seed stored once per group (before the pass for the static
/// regimes, at the group's first row for the dynamic one); PRODSUM -- `acc =
/// acc + a * b` from 0 with emitProdSum's own flags; and the count. The
/// grouping's GkVal then carries the counts and one pool per state (`Agg`)
/// and no permutation. False -- nothing emitted -- when this lane cannot place
/// it (the type's regime differs from the plan's, a non-Int64 key, a source
/// that is not a dense rank-1 axis of the key's extent, an unresolved kernel).
and private emitGroupAggPass (c: Ctx) (id: IRId) (plan: Blade.Optimize.GroupAggPlan) (keyArr: ArrVal) (nsrc: int64)
                             (gkTy: IRType) (readKey: string -> string) (ifThen: string -> (unit -> unit) -> unit)
                             (panicCodeUnless: string -> string -> string -> unit) : bool =
    let regimeHere =
        match gkTy with
        | IRTGroupKeys (outer, _, ev) ->
            (match Blade.IRPrint.tryEvalIntIR outer.Extent, ev with
             | Some n, None -> Some (Blade.Optimize.GaPositional n)
             | Some _, Some vs when vs |> List.forall (function EVInt _ -> true | _ -> false) ->
                 Some (Blade.Optimize.GaEnum (vs |> List.choose (function EVInt n -> Some n | _ -> None)))
             | Some _, Some _ -> None
             | None, _ -> Some Blade.Optimize.GaDynamic)
        | _ -> None
    // the key column must be a stored pool: a producer read once per row here
    // would change the CSR's evaluation count (one read per pass, as written)
    if regimeHere <> Some plan.Regime || keyArr.Elem <> ScI64 then false
    elif (match keyArr.Src with APool _ -> false | _ -> true) then false else
    let srcArrs = plan.States |> List.map (fun st -> st.Sources |> List.map (consumeArrOperand c))
    let dense (a: ArrVal) = match a.Groups with [ GDense n ] -> n = nsrc | _ -> false
    let kernels =
        plan.States |> List.map (fun st ->
            match st.Kind with
            | Blade.Optimize.GaSeeded (fk, _) | Blade.Optimize.GaUnseeded fk ->
                (match resolveCallable fk with Some cl when cl.Params.Length = 2 -> Some cl | _ -> None)
            | _ -> None)
    let placeable =
        srcArrs |> List.forall (List.forall dense)
        && List.forall2 (fun (st: Blade.Optimize.GroupAggState) (k: IRCallable option) ->
                match st.Kind with
                | Blade.Optimize.GaSeeded _ | Blade.Optimize.GaUnseeded _ -> k.IsSome
                | _ -> true) plan.States kernels
    if not placeable then false else
    plan.EmittedLlvm.Value <- true
    let sc (et: ElemType) = if et = ETInt64 then ScI64 else ScF64
    let iv (r: string) = { Reg = r; Ty = ScI64 }
    let i64At (pool: string) (idx: string) = gepCell c ScI64 pool idx
    let isZero (r: string) =
        let z = freshReg c
        ln c (renderCmp { Dest = z; Kind = "icmp"; Pred = "eq"; Ty = ScI64; Lhs = r; Rhs = "0" })
        z
    let ifElse (cond: string) (t: unit -> unit) (e: unit -> unit) =
        let lT = freshLbl c "ga.first"
        let lE = freshLbl c "ga.next"
        let lJ = freshLbl c "ga.join"
        ln c (renderBr { Cond = cond; IfTrue = lT; IfFalse = lE })
        lbl c lT
        t ()
        ln c (sprintf "br label %%%s" lJ)
        lbl c lE
        e ()
        ln c (sprintf "br label %%%s" lJ)
        lbl c lJ
    let zeroOf (ty: Sc) = { Reg = (if ty = ScF64 then f64Const 0.0 else "0"); Ty = ty }
    let incr (p: string) =
        let cur = loadCell c ScI64 p
        storeCell c ScI64 p { Reg = i64Bin c "add" cur.Reg "1"; Ty = ScI64 }
    // one state's step at row `ki`, its accumulator and count cells in hand
    let step (j: int) (st: Blade.Optimize.GroupAggState) (accPtr: string) (cntPtr: string) (ki: string) (dynamic: bool) =
        let ty = sc st.Elem
        let readSrc (k: int) = coerce c ty (readCell c srcArrs.[j].[k] [ iv ki ])
        let firstRow () = isZero (loadCell c ScI64 cntPtr).Reg
        match st.Kind, kernels.[j] with
        | Blade.Optimize.GaUnseeded _, Some cl ->
            let v = readSrc 0
            ifElse (firstRow ())
                (fun () -> storeCell c ty accPtr v)
                (fun () ->
                    let acc = loadCell c ty accPtr
                    storeCell c ty accPtr (coerce c ty (applyKernel c cl [ KScalar acc; KScalar v ])))
        | Blade.Optimize.GaSeeded (_, seed), Some cl ->
            if dynamic then
                ifThen (firstRow ()) (fun () -> storeCell c ty accPtr (coerce c ty (emitExpr c seed)))
            let acc = loadCell c ty accPtr
            let v = readSrc 0
            storeCell c ty accPtr (coerce c ty (applyKernel c cl [ KScalar acc; KScalar v ]))
        | Blade.Optimize.GaProdSum, _ ->
            if dynamic then ifThen (firstRow ()) (fun () -> storeCell c ty accPtr (zeroOf ty))
            let a = readSrc 0
            let b = readSrc 1
            let prod = freshReg c
            ln c (renderBin { Dest = prod; Opcode = (if ty = ScF64 then "fmul" else "mul"); Flags = fmfFor c ty; Ty = ty; Lhs = a.Reg; Rhs = b.Reg })
            // emitProdSum's outer accumulation flags: its `+` is licensed by
            // construction, so the build's polarity decides
            let accFlags = withFoldFmf c (Blade.CodeGenState.fpReassocEnabled ()) (fun () -> fmfFor c ty)
            let cur = loadCell c ty accPtr
            let s = freshReg c
            ln c (renderBin { Dest = s; Opcode = (if ty = ScF64 then "fadd" else "add"); Flags = accFlags; Ty = ty; Lhs = cur.Reg; Rhs = prod })
            storeCell c ty accPtr { Reg = s; Ty = ty }
        | _ -> ()
    let isCount (st: Blade.Optimize.GroupAggState) = (match st.Kind with Blade.Optimize.GaCount -> true | _ -> false)
    let prefixOffsets (ngOp: string) (counts: string) =
        let ngPlus1 =
            match tryLitI64 ngOp with
            | Some n -> string (n + 1L)
            | None -> i64Bin c "add" ngOp "1"
        let offs = allocPool c ScI64 ngPlus1
        storeCell c ScI64 (i64At offs "0") { Reg = "0"; Ty = ScI64 }
        emitCountedLoopTo c ngOp (fun gi ->
            let o = loadCell c ScI64 (i64At offs gi)
            let n = loadCell c ScI64 (i64At counts gi)
            storeCell c ScI64 (i64At offs (i64Bin c "add" gi "1")) { Reg = i64Bin c "add" o.Reg n.Reg; Ty = ScI64 })
        offs
    match plan.Regime with
    | Blade.Optimize.GaDynamic ->
        let slot = allocaOf c "[128 x i8], align 8"
        needShim c "blade_gkmap_init_llvm"
        let okInit = freshReg c
        ln c ($"{okInit} = call i32 @blade_gkmap_init_llvm(ptr {slot}, i64 {plan.States.Length + 1})")
        let okB = freshReg c
        ln c ($"{okB} = icmp ne i32 {okInit}, 0")
        panicCodeUnless "BL8005" okB "group_keys: the key -> group map could not be allocated"
        needShim c "blade_gkmap_group_llvm"
        needShim c "blade_gkmap_record_llvm"
        let recordOf (g: string) =
            let r = freshReg c
            ln c ($"{r} = call ptr @blade_gkmap_record_llvm(ptr {slot}, i64 {g})")
            r
        emitCountedLoop c nsrc (fun ki ->
            let k = readKey ki
            let ok = freshReg c
            ln c (renderCmp { Dest = ok; Kind = "icmp"; Pred = "sge"; Ty = ScI64; Lhs = k; Rhs = "0" })
            ifThen ok (fun () ->
                let g = freshReg c
                ln c ($"{g} = call i64 @blade_gkmap_group_llvm(ptr {slot}, i64 {k})")
                let gok = freshReg c
                ln c (renderCmp { Dest = gok; Kind = "icmp"; Pred = "sge"; Ty = ScI64; Lhs = g; Rhs = "0" })
                panicCodeUnless "BL8005" gok "group_keys: the key -> group map could not grow"
                let rcd = recordOf g
                let cntPtr = i64At rcd "0"
                plan.States |> List.iteri (fun j st ->
                    if not (isCount st) then step j st (i64At rcd (string (j + 1))) cntPtr ki true)
                incr cntPtr))
        needShim c "blade_gkmap_count_llvm"
        let ng = freshReg c
        ln c ($"{ng} = call i64 @blade_gkmap_count_llvm(ptr {slot})")
        let counts = allocPool c ScI64 ng
        let pools =
            plan.States |> List.map (fun st -> if isCount st then "" else allocPool c (sc st.Elem) ng)
        emitCountedLoopTo c ng (fun g ->
            let rcd = recordOf g
            storeCell c ScI64 (i64At counts g) (loadCell c ScI64 (i64At rcd "0"))
            plan.States |> List.iteri (fun j st ->
                if not (isCount st) then
                    let ty = sc st.Elem
                    storeCell c ty (gepCell c ty pools.[j] g) (loadCell c ty (i64At rcd (string (j + 1))))))
        needShim c "blade_gkmap_fini_llvm"
        ln c ($"call void @blade_gkmap_fini_llvm(ptr {slot})")
        let offs = prefixOffsets ng counts
        c.GroupKeys.[id] <- { NGroups = ng; Offsets = offs; Perm = "null"; NSrc = nsrc; Agg = Some (counts, pools) }
        true
    | Blade.Optimize.GaPositional _ | Blade.Optimize.GaEnum _ ->
        let (ngroups, bucket, dropNeg) : int64 * (string -> string) * bool =
            match plan.Regime with
            | Blade.Optimize.GaEnum vals ->
                let bucket (kReg: string) =
                    let mutable acc = "0"
                    let mutable found = "false"
                    vals |> List.iteri (fun i v ->
                        let eq = freshReg c
                        ln c (renderCmp { Dest = eq; Kind = "icmp"; Pred = "eq"; Ty = ScI64; Lhs = kReg; Rhs = string v })
                        let sel = freshReg c
                        ln c (sprintf "%s = select i1 %s, i64 %d, i64 %s" sel eq i acc)
                        acc <- sel
                        let f = freshReg c
                        ln c (sprintf "%s = or i1 %s, %s" f found eq)
                        found <- f)
                    panicCodeUnless "BL8006" found "index out of bounds: a group_keys key outside its EnumIdx"
                    acc
                (int64 vals.Length, bucket, false)
            | Blade.Optimize.GaPositional n ->
                let bucket (kReg: string) =
                    let ok = freshReg c
                    ln c (renderCmp { Dest = ok; Kind = "icmp"; Pred = "slt"; Ty = ScI64; Lhs = kReg; Rhs = string n })
                    panicCodeUnless "BL8006" ok ($"index out of bounds: a group_keys key outside 0 .. {n - 1L}")
                    kReg
                (n, bucket, true)
            | Blade.Optimize.GaDynamic -> (0L, (fun k -> k), true)
        let ngOp = string ngroups
        let counts = allocPool c ScI64 ngOp
        let pools =
            plan.States |> List.map (fun st -> if isCount st then "" else allocPool c (sc st.Elem) ngOp)
        emitCountedLoop c ngroups (fun g ->
            storeCell c ScI64 (i64At counts g) { Reg = "0"; Ty = ScI64 }
            plan.States |> List.iteri (fun j st ->
                let ty = sc st.Elem
                match st.Kind with
                | Blade.Optimize.GaSeeded (_, seed) ->
                    storeCell c ty (gepCell c ty pools.[j] g) (coerce c ty (emitExpr c seed))
                | Blade.Optimize.GaProdSum ->
                    storeCell c ty (gepCell c ty pools.[j] g) (zeroOf ty)
                | _ -> ()))
        emitCountedLoop c nsrc (fun ki ->
            let k = readKey ki
            let body () =
                let b = bucket k
                let cntPtr = i64At counts b
                plan.States |> List.iteri (fun j st ->
                    if not (isCount st) then step j st (gepCell c (sc st.Elem) pools.[j] b) cntPtr ki false)
                incr cntPtr
            if dropNeg then
                let ok = freshReg c
                ln c (renderCmp { Dest = ok; Kind = "icmp"; Pred = "sge"; Ty = ScI64; Lhs = k; Rhs = "0" })
                ifThen ok body
            else body ())
        let offs = prefixOffsets ngOp counts
        c.GroupKeys.[id] <- { NGroups = ngOp; Offsets = offs; Perm = "null"; NSrc = nsrc; Agg = Some (counts, pools) }
        true

/// A grouped array of an aggregate-only grouping whose one pass this lane
/// placed: the ragged pair over the grouping's offsets with NO pool (every
/// consumer is a peel that reads its state; the rows are never read).
and private bindGroupAggRows (c: Ctx) (id: IRId) (values: IRExpr) (grouping: IRExpr) : unit =
    let gk = gkOf c grouping
    // FAIL CLOSED (Codex diff review of W3-1, finding 1): the rows are skipped
    // only over a source whose cells already exist -- the plan proved it
    // stored; a source this lane holds as anything but a pool (a producer the
    // gather would have RUN) refuses the program instead of dropping that
    // evaluation (the C++ lane then runs it, forcing as written).
    (match (consumeArrOperand c values).Src with
     | APool _ -> ()
     | _ -> refuse "an aggregate-only grouping whose value source is not a stored pool in this lane (its evaluation cannot be skipped)")
    let elem =
        match Blade.IR.stripUnits (typeOf values) with
        | ArrayElem at -> (match scalarTyOf at.ElemType with Some s -> s | None -> refuse "a group_by over non-scalar values")
        | _ -> refuse "a group_by over a non-array value"
    bindArray c id "" { Elem = elem; Groups = [ GRagged (gk.NGroups, RtDynamic gk.Offsets) ]; Src = APool "null"; RowOpBytes = 0L }

/// A PEEL of an aggregate-only grouping whose one pass this lane placed
/// (the C++ lane's genGroupAggPeelCpp twin): the COUNT from the offsets, an
/// UNSEEDED reduce's BL8003 for the FIRST empty group in group order at this
/// declaration, then the state's pool as the peel's stored output. False when
/// the binding is no planned peel or the pass was not placed.
and private tryBindGroupAggPeel (c: Ctx) (id: IRId) : bool =
    match Blade.Optimize.GroupAggTable.tryPeel id with
    | Some (plan, j) when plan.EmittedLlvm.Value ->
        bindGroupAggPeelIn c id plan j
    | _ -> false

and private bindGroupAggPeelIn (c: Ctx) (id: IRId) (plan: Blade.Optimize.GroupAggPlan) (j: int) : bool =
        (match c.GroupKeys.TryGetValue plan.GkId with
         | true, ({ Agg = Some (counts, pools) } as gk) ->
             let st = plan.States.[j]
             let ty = if st.Elem = ETInt64 then ScI64 else ScF64
             let pool =
                 match st.Kind with
                 | Blade.Optimize.GaCount ->
                     let p = allocPool c ScI64 gk.NGroups
                     emitCountedLoopTo c gk.NGroups (fun g ->
                         let lo = loadCell c ScI64 (gepCell c ScI64 gk.Offsets g)
                         let hi = loadCell c ScI64 (gepCell c ScI64 gk.Offsets (i64Bin c "add" g "1"))
                         storeCell c ScI64 (gepCell c ScI64 p g) { Reg = i64Bin c "sub" hi.Reg lo.Reg; Ty = ScI64 })
                     p
                 | Blade.Optimize.GaUnseeded _ ->
                     // where the peel's own reduce raised it: the ordinary
                     // peel runs the kernel body inlined, frameless (gap G76:
                     // one frame list, the C++ lane's and the interpreter's),
                     // so the planned check pushes no kernel frame either
                     emitCountedLoopTo c gk.NGroups (fun g ->
                         let n = loadCell c ScI64 (gepCell c ScI64 counts g)
                         let isEmpty = freshReg c
                         ln c ($"{isEmpty} = icmp eq i64 {n.Reg}, 0")
                         let lEmpty = freshLbl c "ga.empty"
                         let lOk = freshLbl c "ga.ok"
                         ln c (renderBr { Cond = isEmpty; IfTrue = lEmpty; IfFalse = lOk })
                         lbl c lEmpty
                         needShim c "blade_panic_at"
                         let msg = stringGlobal c "error[BL8003]: reduce: empty array, no reduction possible"
                         ln c (renderCall { Dest = None; RetTy = ScVoid; Callee = "@blade_panic_at"; Args = [ ScStr, msg ] @ declLocArgs c })
                         ln c "unreachable"
                         lbl c lOk)
                     pools.[j]
                 | _ -> pools.[j]
             bindArray c id "" { Elem = ty; Groups = [ denseOfOp gk.NGroups ]; Src = APool pool; RowOpBytes = 0L }
             true
         | _ -> false)

/// Resolve a grouping operand to its emitted tables. BL3017 makes the bare
/// name the only legal spelling, so anything else here is an escape.
and private gkOf (c: Ctx) (grouping: IRExpr) : GkVal =
    match grouping with
    | IRVar (gkid, _) ->
        (match c.GroupKeys.TryGetValue gkid with
         | true, gk -> gk
         | _ -> refuse "a grouping read before its group_keys binding was emitted")
    | _ -> refuse "a grouping that is not a bound group_keys name"

/// `IRLet` with the let's own continuation in hand -- which is where a
/// FUNCTION-BODY `let gk = group_keys(...)` recovers its bucketing regime: a
/// body let is a bare (id, value) pair and IRGroupKeys types as unit, but
/// every legal MENTION of the grouping is the bare name, stamped by Lowering
/// with the inferred IRTGroupKeys. Same recovery, same reason, as the C++
/// lane's groupKeysTypeInScope (CodeGen.fs) -- guessing the regime instead
/// would silently renumber buckets (positional counts empty groups, dynamic
/// numbers first-occurrence). A grouping nobody mentions needs no tables.
and private bindLetIn (c: Ctx) (id: IRId) (value: IRExpr) (scope: IRExpr) : unit =
    match value with
    | IRGroupKeys keys ->
        let mutable found : IRType option = None
        iterIRExpr
            (fun n ->
                match n with
                | IRVar (vid, (IRTGroupKeys _ as ty)) when vid = id && found.IsNone -> found <- Some ty
                | _ -> ())
            scope
        (match found with
         | Some ty -> emitGroupKeysBinding c id keys ty
         | None -> ())
    // Aggregate-only grouping (Blade.Optimize.planGroupAggregate): once the
    // grouping placed its one pass, its grouped arrays carry no rows and its
    // peels read their states.
    | IRGroupBy (values, grouping) when (match Blade.Optimize.GroupAggTable.tryGroupBy id with
                                         | Some p -> p.EmittedLlvm.Value
                                         | None -> false) ->
        bindGroupAggRows c id values grouping
    | IRCompute _ when tryBindGroupAggPeel c id -> ()
    | _ -> bindLet c id value

/// Bind a NAME to a value: the one entry point for both `IRLet` and a
/// top-level binding, so the two cannot drift on the copy rule below.
///
/// COPY-ON-ALIAS: an assignable binding initialized from an EXISTING stored
/// array deep-copies its pool. Every Blade binding except `static` is
/// assignable, so `let b = Z` followed by `b(0) = ...` must not reach into
/// Z's storage -- the C++ lane learned this the hard way (the mut-aliasing
/// bug pinned by loops/081). A deferred producer is exempt: it has no storage
/// to share, and copying it would materialize -- and therefore PRINT -- a
/// computation the program never forced.
and private bindNamed (c: Ctx) (id: IRId) (name: string) (declTy: IRType) (value: IRExpr) : unit =
    let rec peelCompute (e: IRExpr) = match e with IRCompute i -> peelCompute i | e -> e
    match value with
    // Sibling maps (Optimize.planSiblingMaps, round 4 wave 2, PM04): a planned
    // member allocates and binds its pool; the last member fills every pool
    // in one sweep (tryBindSiblingMember).
    | IRCompute (IRApplyCombinator ainfo) when (Blade.Optimize.SiblingMapTable.tryMember id).IsSome && tryBindSiblingMember c id name ainfo -> ()
    | IRBeginPoolScope call ->
        ln c ($"; A01: begin inline pool scope: {call}")
        pushPoolScope c
        c.PoolScopes.[c.PoolScopes.Count - 1].InlineId <- Some id
    | IRReleasePools _ -> emitExpr c value |> ignore
    | IRGroupKeys keys ->
        // A module-scope grouping carries its regime on the binding record;
        // the sentinel itself has no runtime value and prints nothing.
        (match declTy with
         | IRTGroupKeys _ -> emitGroupKeysBinding c id keys declTy
         | _ -> refuse "a group_keys binding whose regime type is unavailable")
    // Compaction fusion (Blade.Optimize.planCompactionFusion): a planned mask
    // counts its kept cells in its own pass (B4b; emitCompoundInit then skips
    // the count pass), and the lifted `y * y` of a planned fold stays a
    // PRODUCER (B4a): its one reader, the fold, runs the self-product over
    // `y`'s pool cell by cell -- no square pool.
    | IRMask (arrExpr, predExpr) when (match Blade.Optimize.CompactionTable.tryMask id with
                                       | Some plan -> tryBindCountedMask c id name plan arrExpr predExpr
                                       | None -> false) -> ()
    | IRCompute _ when (Blade.Optimize.CompactionTable.tryFoldMap id).IsSome
                       && (match peelCompute value with IRApplyCombinator _ -> true | _ -> false) ->
        bindNamed c id name declTy (peelCompute value)
    // Compact row classes (Blade.Optimize.planCompactRowClasses): the planned
    // compact PRODUCER fills its pool row by row and stores each packed row's
    // orbit-class partials right behind it; the planned CONSUMER combines
    // them in row order. Both fall through when the lane cannot take it.
    | IRCompute _ when (match peelCompute value with
                        | IRApplyCombinator pinfo ->
                            (match Blade.Optimize.CompactRowTable.tryProducer id with
                             | Some plan -> tryBindCompactRowProducer c id name plan pinfo
                             | None -> false)
                        | _ -> false) -> ()
    | IRReduceCompute _ when (match Blade.Optimize.CompactRowTable.tryConsumer id with
                              | Some plan -> plan.EmittedLlvm.Value.IsSome
                              | None -> false) ->
        let plan = (Blade.Optimize.CompactRowTable.tryConsumer id).Value
        match compactRowConsumer c plan with
        | Some v ->
            let target = match scalarTyOf declTy with Some t when t <> ScVoid -> t | _ -> v.Ty
            bindScalar c id name (coerce c target v)
        | None -> refuse "compact row classes: the consumer's orbit plan no longer matches its producer"
    // Hot-row fold scheduling (Blade.Optimize.planHotRowFolds): the planned
    // PRODUCER fills its pool row by row and runs the consumer's row map on
    // each row as it completes; the planned CONSUMER folds those row sums.
    // Both arms fall through to the ordinary paths when the lane cannot
    // take the schedule (the producer then publishes no row-sum slot).
    | IRCompute _ when (match peelCompute value with
                        | IRApplyCombinator pinfo ->
                            (match Blade.Optimize.HotRowTable.tryProducer id with
                             | Some plan when (match plan.Form with Blade.Optimize.RowFold -> true | _ -> false) ->
                                tryBindHotRowProducer c id name plan pinfo
                             | Some plan when (match plan.Form with Blade.Optimize.ChunkFold -> true | _ -> false) ->
                                tryBindFlatEpilogueProducer c id name plan pinfo
                             | _ -> false)
                        | _ -> false) -> ()
    | IRReduceCompute (_, k, seed) when (match Blade.Optimize.HotRowTable.tryConsumer id with
                                         | Some plan -> c.ArrSlots.ContainsKey plan.RowSumId
                                         | None -> false) ->
        let plan = (Blade.Optimize.HotRowTable.tryConsumer id).Value
        emitHotRowConsumer c id name declTy plan k seed
    // B1 (round-2 cell epilogues): the producer's fused loop already folded
    // this binding's cells into the published accumulator.
    | IRReduceCompute _ when (match Blade.Optimize.HotRowTable.tryConsumer id with
                              | Some plan -> (match plan.Form with Blade.Optimize.ChunkFold -> true | _ -> false)
                                             && c.Slots.ContainsKey plan.RowSumId
                              | None -> false) ->
        let plan = (Blade.Optimize.HotRowTable.tryConsumer id).Value
        let (slot, ty) = c.Slots.[plan.RowSumId]
        c.Slots.Remove plan.RowSumId |> ignore
        let v = loadSlot c slot ty
        let target = match scalarTyOf declTy with Some t when t <> ScVoid -> t | _ -> v.Ty
        bindScalar c id name (coerce c target v)
    // A7c (gap G69): a join whose legs rode the producer's fused loop -- its
    // tuple is the legs' published slots, bound here, at the join's own
    // statement (a producer that did not carry them published none, and the
    // join falls through to its ordinary pass).
    | IRReduceCompute _ when (match Blade.Optimize.EpilogueLegTable.tryJoin id with
                              | Some lp -> lp.Legs |> List.forall (fun l -> c.Slots.ContainsKey l.SlotId)
                              | None -> false) ->
        let lp = (Blade.Optimize.EpilogueLegTable.tryJoin id).Value
        let vs =
            lp.Legs |> List.map (fun l ->
                let (slot, ty) = c.Slots.[l.SlotId]
                c.Slots.Remove l.SlotId |> ignore
                VScalar (loadSlot c slot ty))
        c.TupleSlots.[id] <- vs
    | IRReduceCompute _ when (match Blade.Optimize.EpilogueLegTable.tryJoin id with
                              | Some lp -> not lp.DecidedLlvm.Value
                              | None -> false) ->
        let lp = (Blade.Optimize.EpilogueLegTable.tryJoin id).Value
        lp.DecidedLlvm.Value <- true
        if Blade.Effects.Decisions.active () then
            Blade.Effects.Decisions.record
                { Rule = "epilogue-legs"; Version = 1; Span = c.DeclSpan; Subject = lp.Subject
                  Outcome = Blade.Effects.Declined "llvm lane: the producer's fused loop did not carry the legs (its cell epilogue was not taken in this lane)"
                  Evidence = [ "the legs keep their joined pass" ] @ (match lp.InlinedCall with Some call -> [ $"{call} stays inlined (planned for these legs under the OPT-IN BLADE_EPILOGUE_LEGS=1 arm, whose lifetime audit is incomplete, G69); its IR lifetime region still closes the inline allocation scope at its IRReleasePools event when the fused form declines" ] | None -> []) }
        bindNamed c id name declTy value
    // B2r for a recursion: the step's cell loop carried this prodsum's chain.
    | IRProdSum _ when (match Blade.Optimize.CellFoldTable.tryFoldLet id with
                        | Some e -> e.EmittedLlvm.Value.IsSome
                        | None -> false) ->
        let e = (Blade.Optimize.CellFoldTable.tryFoldLet id).Value
        let slot = e.EmittedLlvm.Value.Value
        e.EmittedLlvm.Value <- None
        let v = loadSlot c slot ScF64
        let target = match scalarTyOf declTy with Some t when t <> ScVoid -> t | _ -> v.Ty
        bindScalar c id name (coerce c target v)
    // THE ROW ORBIT ACTION (round 3, D-P2 / gap G73): the direct `map |>
    // compute` spelling of a row action over a stored SymIdx<2, n> pool.
    | IRCompute (IRApplyCombinator ainfo) when tryBindOrbitAction c id name declTy ainfo -> ()
    | _ ->
    match classifyValue c value with
    | VArray a ->
        let stored =
            match value, a.Src with
            // The read-only alias plan (Optimize.planAliasElision, rule
            // `transpose-normal-form v1`): neither name is ever written, so
            // the binding shares the source's pool. Main only, as the C++
            // lane's main top level (the shared pool outlives every reader).
            | IRVar _, APool _ when not c.InFunction && Blade.Optimize.AliasElideTable.contains id -> a
            // Only a RENAME copies. A sub-view binding (`let row = M(0)`) is a
            // VIEW in the C++ lane too -- copying it would break writes made
            // through a `mut` array's peeled row.
            | (IRVar _ | IRParam _), APool _ -> copyArr c a
            // A BLOCK / FUNCTION-BODY assignable alias of a still-DEFERRED
            // array (gap G60 (c), formalism 10.2): only a MODULE-LEVEL alias
            // is the same deferred value; a nested one is a forcing use of
            // its source, the copy made HERE -- the C++ lane's
            // genVarAliasBinding (`not moduleLevel && MutableArrayLets`) and
            // the interpreter's IRLet arm. The source is forced under its own
            // name first (materializeExpr: a module binding forced at main's
            // top level is stored at the statement and prints, a function
            // body's force is private), then the alias copies those cells, so
            // a later write to the source's inputs is never seen through the
            // alias. It used to stay a producer, read after the write.
            | IRVar (src, vty), (AVirt _ | AVirtFlat _)
                when name = "" && Set.contains id (Blade.CodeGenExprSupport.programMutableArrayLetsCell ()).Value
                     && (match Blade.IR.stripUnits vty with ArrayElem _ -> true | _ -> false) ->
                // A MODULE source is stored by a hoist to the current
                // positional statement's start, which must be THIS alias (its
                // let is on the spine: the hoist then lands after every
                // effect the block emitted before it -- an input write, a
                // failing statement). Off the spine (a block inside a branch
                // or a loop body) the hoist would land earlier: refused, fail
                // closed. A local source is forced in place.
                let preForced = not c.SpinePositional && c.StmtPositionalReads.Contains src
                if c.BindingValues.ContainsKey src
                   && not ((preForced || obj.ReferenceEquals(c.SpineNode, value)) && forceHoistable c src) then
                    let nm = defaultArg (Map.tryFind src c.ModuleNames) (string src)
                    refuse ($"a block-local alias of the deferred module binding '{nm}' off its statement's spine (a block inside a branch or a loop body): its force could not be placed at the alias")
                // The ALIAS SITE is the one place in main whose force point
                // is the node itself (the C++ lane forces at the alias): a
                // positional statement here, then the statement's own force
                // boundary restored for every other read (setSpine). The
                // restored baseline no longer matches the upgraded source, so
                // a later hoist in this statement stays inline (fail safe).
                // When the statement ALSO reads the source positionally, the
                // C++ lane's pre-pass forces it at the statement's START and
                // the alias only copies (review codex-diff-W4B-B-3 finding
                // 1): materializeExpr's ordinary hoist does exactly that, so
                // no alias-site point is opened.
                let saved =
                    if c.BindingValues.ContainsKey src && not c.SpinePositional && not preForced then
                        let s = (c.StmtStart, c.StmtSlots, HashSet<IRId>(c.HoistedIds))
                        positionalStatement c
                        Some s
                    else None
                let forced =
                    try materializeExpr c value
                    finally
                        match saved with
                        | Some (st, slots, hoisted) ->
                            c.StmtStart <- st
                            c.StmtSlots <- slots
                            c.HoistedIds.Clear()
                            c.HoistedIds.UnionWith hoisted
                        | None -> ()
                (match forced.Src with
                 | APool _ -> copyArr c forced
                 | _ -> refuse "a block-local alias of a deferred array whose source could not be stored")
            | _ -> a
        bindArray c id name stored
        (match stored.Src with
         | AVirt _ | AVirtFlat _ -> c.DeferredLetValues.[id] <- value
         | _ -> c.DeferredLetValues.Remove id |> ignore)
    | VTuple vs -> c.TupleSlots.[id] <- vs
    | VScalar v ->
        if v.Ty <> ScVoid then
            let target = match scalarTyOf declTy with Some t when t <> ScVoid -> t | _ -> v.Ty
            bindScalar c id name (coerce c target v)
    | VOpaque -> c.LoopObjs.Add id |> ignore

/// SIBLING MAPS (round 4 wave 2, PM04; Blade.Optimize.planSiblingMaps), the
/// LLVM realization: an earlier member's binding allocates its pool and binds
/// it with NO fill (its producer stashed); the LAST member runs ONE serial
/// sweep filling every member's pool, each cell its own producer. Preflighted
/// at the first member (a member the cell epilogue takes in this lane keeps
/// its own pass); a member whose producer is not the planned dense shape
/// fills the stashed pools first, in member order, then takes its own path.
and private siblingLlvmPreflight (g: Blade.Optimize.SiblingMapGroup) : Result<unit, string> =
    match g.Members |> List.tryFindIndex (fun m -> (Blade.Optimize.HotRowTable.tryProducer m).IsSome) with
    | Some j -> Error $"the cell epilogue takes {List.item j g.Names} in this lane (one pass that stores it and folds its consumer)"
    | None -> Ok ()

and private siblingFillInto (c: Ctx) (a: ArrVal) (ptr: string) : unit =
    emitNest c a.Extents (fun idxs ->
        let v = coerce c a.Elem (readCell c a idxs)
        storeCell c a.Elem (gepCell c a.Elem ptr (storageOffset c a.Groups idxs)) v)

and private tryBindSiblingMember (c: Ctx) (id: IRId) (name: string) (ainfo: ApplyInfo) : bool =
    match Blade.Optimize.SiblingMapTable.tryMember id with
    | None -> false
    | Some (g, j) ->
    let cell = siblingLlvmCell ()
    let subject = $"""{g.Region}: {String.concat ", " g.Names}"""
    let record outcome ev =
        if Blade.Effects.Decisions.active () then Blade.Optimize.recordSiblingMaps subject outcome ev
    if j = 0 then
        match siblingLlvmPreflight g with
        | Ok () -> cell.[g.GroupId] <- (true, [], false)
        | Error why ->
            record (Blade.Effects.Declined $"llvm lane: {why}; every member keeps its own pass") []
            cell.[g.GroupId] <- (false, [], true)
    match cell.TryGetValue g.GroupId with
    | true, (true, stash, false) ->
        let start = List.item j g.Starts
        let ext = List.item j g.Extents
        let a = applyToArr c ainfo
        let shapeOk =
            match a.Src with
            | AVirt _ | AVirtFlat _ ->
                isDenseShape a.Groups
                && (match g.Kind with
                    | Blade.Optimize.SibFlat cells -> shapeCells a.Groups = cells
                    | Blade.Optimize.SibHalo _ -> a.Extents = [ ext ])
            | APool _ -> false
        if not shapeOk then
            // fallback: the stashed pools are filled here, in member order
            for (_, sa, sp) in stash do siblingFillInto c sa sp
            cell.[g.GroupId] <- (true, [], true)
            record (Blade.Effects.Declined $"llvm lane: member {j + 1} ({name}) is not the planned dense producer; the earlier members' pools are filled here, individually, before its own") []
            false
        else
        let ptr = allocPool c a.Elem (string (shapeCells a.Groups))
        bindArray c id name { a with Src = APool ptr }
        let stash' = stash @ [ (start, a, ptr) ]
        if j < g.Members.Length - 1 then
            cell.[g.GroupId] <- (true, stash', false)
        else
            cell.[g.GroupId] <- (true, [], true)
            match g.Kind with
            | Blade.Optimize.SibFlat _ ->
                let (_, a0, _) = List.head stash'
                emitNest c a0.Extents (fun idxs ->
                    for (_, sa, sp) in stash' do
                        let v = coerce c sa.Elem (readCell c sa idxs)
                        storeCell c sa.Elem (gepCell c sa.Elem sp (storageOffset c sa.Groups idxs)) v)
                record Blade.Effects.Applied [ $"llvm lane: one serial flat sweep writes {stash'.Length} outputs (this lane has no thread form)" ]
            | Blade.Optimize.SibHalo _ ->
                let spans = stash' |> List.mapi (fun m (s, sa, sp) -> (s, s + List.item m g.Extents, sa, sp))
                let points = spans |> List.collect (fun (s, z, _, _) -> [ s; z ]) |> List.distinct |> List.sort
                for (lo, hi) in List.pairwise points do
                    if hi > lo then
                        let active = spans |> List.filter (fun (s, z, _, _) -> s <= lo && hi <= z)
                        if not active.IsEmpty then
                            emitCountedLoopTo c (string (hi - lo)) (fun k ->
                                for (s, _, sa, sp) in active do
                                    let idx = i64Add c k (string (lo - s))
                                    let v = coerce c sa.Elem (readCell c sa [ { Reg = idx; Ty = ScI64 } ])
                                    storeCell c sa.Elem (gepCell c sa.Elem sp idx) v)
                record Blade.Effects.Applied [ $"llvm lane: one serial halo walk writes {stash'.Length} outputs, peeled by member domain (this lane has no thread form)" ]
        true
    | _ -> false

/// Publish a top-level binding that some out-of-line function reads (see
/// computeModuleReads). A SCALAR moves into its global outright: main's slot
/// entry re-points at the global, so every later main-side read and store --
/// and every function's -- goes through one storage location, and a write
/// from a function body is visible to main exactly as the C++ lane's
/// by-reference capture makes it. An ARRAY publishes its pool pointer, and
/// only a STORED, statically shaped one: a deferred producer would have to be
/// forced at a demand point this lane does not track, and a ragged or
/// runtime-extent shape carries operands a bare pointer cannot. Those stay
/// unpublished, and a call that needs them refuses (emitCall).
and private publishBinding (c: Ctx) (id: IRId) : unit =
    if c.PublishWanted.Contains id then
        let sym = $"@bl_mod_{id}"
        match c.Slots.TryGetValue id with
        | true, (slot, sc) when sc <> ScVoid ->
            if not (c.Published.ContainsKey id) then
                c.Globals.Add($"{sym} = internal global {(llTy sc)} zeroinitializer")
            if slot <> sym then
                let v = loadSlot c slot sc
                ln c ($"store {(llTy sc)} {v.Reg}, ptr {sym}")
                c.Slots.[id] <- (sym, sc)
            c.Published.[id] <- PubScalar (sym, sc)
        | _ ->
            match c.ArrSlots.TryGetValue id with
            | true, ({ Src = APool p } as a)
                when a.Groups |> List.forall (function GDense _ | GSym _ | GAnti _ -> true | _ -> false) ->
                if not (c.Published.ContainsKey id) then
                    c.Globals.Add($"{sym} = internal global ptr null")
                ln c ($"store ptr {p}, ptr {sym}")
                c.Published.[id] <- PubArray (sym, a.Elem, a.Groups)
            | _ -> c.Published.Remove id |> ignore

and private bindLet (c: Ctx) (id: IRId) (value: IRExpr) : unit =
    bindNamed c id "" (typeOf value) value

/// The compact row-class PRODUCER (Blade.Optimize.planCompactRowClasses, B3;
/// the C++ lane's CompactRowEpilogue twin): the rank-2 compact pool is filled
/// one packed row at a time -- the serial triangle's cells, in pool order,
/// from the same per-cell closure the ordinary materialize runs -- and right
/// after each row its orbit-class partials are computed from zero (the fold's
/// `+`, with its licence flags) and stored into one rank-1 pool per class.
/// The orbit plan is the one the consumer's fold yields (same recogniser,
/// same inputs). False -- nothing emitted, nothing published -- for a walk
/// the orbit fold does not take, a body that may abort, a tile-dispatched
/// producer, or a shape other than the rank-2 group.
and private tryBindCompactRowProducer (c: Ctx) (id: IRId) (name: string) (plan: Blade.Optimize.CompactRowPlan) (pinfo: ApplyInfo) : bool =
    plan.EmittedLlvm.Value <- None
    let srcName i = match Map.tryFind i c.ModuleNames with Some n -> n | None -> $"__v{i}"
    match Blade.OrbitFold.recognise (fun e -> e) resolveCallable srcName plan.ConsumerComp plan.ConsumerKernel plan.ConsumerSeed with
    | Blade.OrbitFold.Orbit p when p.SourceId = id && p.Rank = 2 && not (Blade.Optimize.CompactRowTable.bodyMayFail p.Body)
                                   && Blade.Optimize.CompactRowTable.bodyDominated plan Blade.OrbitFold.readVarId p.Body ->
        (match scalarTyOf (IRTScalar p.AccTy) with
         | Some accTy when accTy = ScF64 || accTy = ScI64 ->
             let a = applyToArr c pinfo
             let tiled = (match syrkCandidates.TryGetValue a with | true, _ -> true | _ -> false) || (match rowTileCandidates.TryGetValue a with | true, _ -> true | _ -> false)
             (match a.Src, soleSimplexR a.Groups with
              | (AVirt _ | AVirtFlat _), Some (2, n, strict) when not tiled && n = p.Extent && strict = p.Strict ->
                  let fold = (List.last p.Levels).Fold
                  let licensed = foldFmfDecorable c fold
                  let negBody = Blade.OrbitFold.negatedBody p
                  let ptr = allocPool c a.Elem (string (shapeCells a.Groups))
                  let parts = [ for _ in 1 .. p.AccCount -> allocPool c accTy (string n) ]
                  let evalAt (body: IRExpr) (v: Val) : Val =
                      let slot = allocaOf c (llTy v.Ty)
                      ln c ($"store {(llTy v.Ty)} {v.Reg}, ptr {slot}")
                      let rv = Blade.OrbitFold.readVarId
                      let saved = match c.Slots.TryGetValue rv with | true, s -> Some s | _ -> None
                      c.Slots.[rv] <- (slot, v.Ty)
                      let res = coerce c accTy (emitExpr c body)
                      (match saved with
                       | Some s -> c.Slots.[rv] <- s
                       | None -> c.Slots.Remove rv |> ignore)
                      res
                  let zero = coerce c accTy (emitExpr c (if accTy = ScF64 then IRLit (IRLitFloat 0.0) else IRLit (IRLitInt 0L)))
                  let s = if strict then 1L else 0L
                  emitCountedLoop c n (fun i ->
                      let rb = emitRowBase2 c n strict i
                      let len = i64Bin c "sub" (string (n - s)) i
                      emitCountedLoopTo c len (fun q ->
                          let v = coerce c a.Elem (readCell c a [ { Reg = i; Ty = ScI64 }; { Reg = q; Ty = ScI64 } ])
                          storeCell c a.Elem (gepCell c a.Elem ptr (i64Add c rb q)) v)
                      // The row's partial over cells [from, len), from zero.
                      let rowSum (body: IRExpr) (from: string) (dst: string) =
                          let acc = allocaOf c (llTy accTy)
                          ln c ($"store {(llTy accTy)} {zero.Reg}, ptr {acc}")
                          let trips = if from = "0" then len else i64Bin c "sub" len from
                          emitCountedLoopTo c trips (fun k ->
                              let x = loadCell c a.Elem (gepCell c a.Elem ptr (i64Add c rb (i64Add c from k)))
                              let f = evalAt body x
                              let cur = loadSlot c acc accTy
                              let next = withFoldFmf c licensed (fun () -> coerce c accTy (applyKernel c fold [ KScalar cur; KScalar f ]))
                              ln c ($"store {(llTy accTy)} {next.Reg}, ptr {acc}"))
                          storeCell c accTy (gepCell c accTy dst i) (loadSlot c acc accTy)
                      if strict then
                          rowSum p.Body "0" parts.[0]
                          if p.AccCount = 2 then rowSum negBody "0" parts.[1]
                      else
                          let d = loadCell c a.Elem (gepCell c a.Elem ptr rb)
                          storeCell c accTy (gepCell c accTy parts.[0] i) (evalAt p.Body d)
                          rowSum p.Body "1" parts.[1])
                  bindArray c id name { a with Src = APool ptr }
                  plan.EmittedLlvm.Value <- Some parts
                  true
              | _ -> false)
         | _ -> false)
    | _ -> false

/// The compact row-class CONSUMER: each class total is its rank-1 pool of row
/// partials summed in ASCENDING ROW ORDER from zero (no licence flags), then
/// the orbit plan's addition-only combine and outer seed (orbitCombineLlvm).
and private compactRowConsumer (c: Ctx) (plan: Blade.Optimize.CompactRowPlan) : Val option =
    let srcName i = match Map.tryFind i c.ModuleNames with Some n -> n | None -> $"__v{i}"
    match Blade.OrbitFold.recognise (fun e -> e) resolveCallable srcName plan.ConsumerComp plan.ConsumerKernel plan.ConsumerSeed, plan.EmittedLlvm.Value with
    | Blade.OrbitFold.Orbit p, Some parts when p.SourceId = plan.ProducerId && parts.Length = p.AccCount ->
        (match scalarTyOf (IRTScalar p.AccTy), scalarTyOf (IRTScalar p.ElemTy) with
         | Some accTy, Some elem ->
             let fold = (List.last p.Levels).Fold
             let zero = coerce c accTy (emitExpr c (if accTy = ScF64 then IRLit (IRLitFloat 0.0) else IRLit (IRLitInt 0L)))
             let totals =
                 parts |> List.map (fun pool ->
                     let acc = allocaOf c (llTy accTy)
                     ln c ($"store {(llTy accTy)} {zero.Reg}, ptr {acc}")
                     emitCountedLoop c p.Extent (fun i ->
                         let x = loadCell c accTy (gepCell c accTy pool i)
                         let cur = loadSlot c acc accTy
                         let next = withFoldFmf c false (fun () -> coerce c accTy (applyKernel c fold [ KScalar cur; KScalar x ]))
                         ln c ($"store {(llTy accTy)} {next.Reg}, ptr {acc}"))
                     loadSlot c acc accTy)
             Some (orbitCombineLlvm c p elem accTy totals)
         | _ -> None)
    | _ -> None

/// THE COMPACTION PASS LOOP of this lane (the C++ lane's
/// CodeGenExprSupport.compactionPassLoop): `step i` handles one source cell
/// (it advances the cursor slot). With `feeds` (fold callable, accumulator
/// slot) -- the serial chains of the planned `reduce(y * y, (+))` folds
/// (B4c) -- the loop is blocked by 256 source cells and after each block
/// every chain advances over the compact cells that block completed (index <
/// cursor; the slack cell is never read): the first kept square peeled as
/// the chain's start, then `acc = k(acc, v * v)` in kept order. The product
/// carries NO fast-math flag, so it cannot contract into the add -- the
/// rounding a stored `y * y` cell has, under every contraction mode. Shared
/// by B4's compound-site pass and T4a's mask-site pass, so the chain is the
/// same wherever it runs.
and private emitCompactionPassLlvm (c: Ctx) (n: string) (dst: string) (cursor: string) (feeds: (IRCallable * string) list) (step: string -> unit) : unit =
    if feeds.IsEmpty then
        emitCountedLoopTo c n step
    else
        emitCompactionBlocksLlvm c n dst cursor feeds (fun lo trips ->
            emitCountedLoopTo c trips (fun k -> step (i64Add c lo k)))

/// The pass in 256-cell BLOCKS: `block lo trips` emits one block [lo, lo +
/// trips), and the chains (when fed) advance after each block, as above.
and private emitCompactionBlocksLlvm (c: Ctx) (n: string) (dst: string) (cursor: string) (feeds: (IRCallable * string) list) (block: string -> string -> unit) : unit =
    emitCompactionBlocksLlvmMulti c n cursor (feeds |> List.map (fun (k, acc) -> (k, acc, dst))) block

/// The per-cell pass over several companion pools (T4c's joint pass).
and private emitCompactionPassLlvmMulti (c: Ctx) (n: string) (cursor: string) (feeds: (IRCallable * string * string) list) (step: string -> unit) : unit =
    if feeds.IsEmpty then
        emitCountedLoopTo c n step
    else
        emitCompactionBlocksLlvmMulti c n cursor feeds (fun lo trips ->
            emitCountedLoopTo c trips (fun k -> step (i64Add c lo k)))

/// `emitCompactionBlocksLlvm` whose chains may each fold a DIFFERENT pool
/// (the shared selection, T4c: every companion's pool advances on the one
/// cursor, so one completed-cell counter serves every chain). `feeds` =
/// (kernel, accumulator slot, the pool it folds); each pool's square is
/// formed once per cell and fed to that pool's chains in feed order (one
/// pool: exactly the single-pool emission).
and private emitCompactionBlocksLlvmMulti (c: Ctx) (n: string) (cursor: string) (feeds: (IRCallable * string * string) list) (block: string -> string -> unit) : unit =
    let fSlot =
        if feeds.IsEmpty then ""
        else
            let s = allocaOf c "i64"
            ln c ($"store i64 0, ptr {s}")
            s
    let pools = feeds |> List.map (fun (_, _, d) -> d) |> List.distinct
    let feedsOf (d: string) = feeds |> List.filter (fun (_, _, d2) -> d2 = d) |> List.map (fun (k, acc, _) -> (k, acc))
    let squareOf (dst: string) (j: string) : Val =
        let v = loadCell c ScF64 (gepCell c ScF64 dst j)
        let p = freshReg c
        ln c (renderBin { Dest = p; Opcode = "fmul"; Flags = ""; Ty = ScF64; Lhs = v.Reg; Rhs = v.Reg })
        { Reg = p; Ty = ScF64 }
    let nb = i64Bin c "udiv" (i64Add c n "255") "256"
    emitCountedLoopTo c nb (fun bi ->
        let lo = i64Bin c "mul" bi "256"
        let hiRaw = i64Add c lo "256"
        let lt = freshReg c
        ln c (renderCmp { Dest = lt; Kind = "icmp"; Pred = "slt"; Ty = ScI64; Lhs = hiRaw; Rhs = n })
        let hi = freshReg c
        ln c ($"{hi} = select i1 {lt}, i64 {hiRaw}, i64 {n}")
        let trips = i64Bin c "sub" hi lo
        block lo trips
        if not feeds.IsEmpty then
            // The chain over the cells this block completed (index < q).
            let f0 = loadSlot c fSlot ScI64
            let q = loadSlot c cursor ScI64
            let anyNew = freshReg c
            ln c (renderCmp { Dest = anyNew; Kind = "icmp"; Pred = "ult"; Ty = ScI64; Lhs = f0.Reg; Rhs = q.Reg })
            let lFeed = freshLbl c "cfuse.feed"
            let lPeel = freshLbl c "cfuse.peel"
            let lChain = freshLbl c "cfuse.chain"
            let lDone = freshLbl c "cfuse.done"
            ln c (renderBr { Cond = anyNew; IfTrue = lFeed; IfFalse = lDone })
            lbl c lFeed
            let first = freshReg c
            ln c (renderCmp { Dest = first; Kind = "icmp"; Pred = "eq"; Ty = ScI64; Lhs = f0.Reg; Rhs = "0" })
            ln c (renderBr { Cond = first; IfTrue = lPeel; IfFalse = lChain })
            lbl c lPeel
            for d in pools do
                let p0 = squareOf d "0"
                for (_, acc) in feedsOf d do ln c ($"store double {p0.Reg}, ptr {acc}")
            ln c ($"store i64 1, ptr {fSlot}")
            ln c (sprintf "br label %%%s" lChain)
            lbl c lChain
            let f1 = loadSlot c fSlot ScI64
            let trips2 = i64Bin c "sub" q.Reg f1.Reg
            emitCountedLoopTo c trips2 (fun k ->
                let j = i64Add c f1.Reg k
                for d in pools do
                    let p = squareOf d j
                    for (cl, acc) in feedsOf d do
                        let cur = loadSlot c acc ScF64
                        let next = withFoldFmf c false (fun () -> coerce c ScF64 (applyKernel c cl [ KScalar cur; KScalar p ]))
                        ln c ($"store double {next.Reg}, ptr {acc}"))
            ln c ($"store i64 {q.Reg}, ptr {fSlot}")
            ln c (sprintf "br label %%%s" lDone)
            lbl c lDone)

/// The in-pass chains of a compound's planned folds in this lane: each fold
/// whose form here is the SERIAL chain (BLADE_FP_REPRO=1, or no licence) and
/// that the plan let into the pass, with a fresh accumulator slot.
and private compactionFeedsLlvm (c: Ctx) (folds: Blade.Optimize.CompactionFoldPlan list) : (Blade.Optimize.CompactionFoldPlan * IRCallable * string) list =
    folds |> List.choose (fun f ->
        if not f.InPass then None
        else
        match resolveCallable f.FoldKernel with
        | Some cl when cl.Params.Length = 2 && not (foldFmfDecorable c cl) ->
            let acc = allocaOf c (llTy ScF64)
            ln c ($"store double 0x0000000000000000, ptr {acc}")
            Some (f, cl, acc)
        | _ -> None)

/// A planned mask (compaction fusion, B4b): the Bool presence pool exactly as
/// the IRMask arm of emitArr materializes it -- one pass, pred(x[i]) stored --
/// plus the count of kept cells taken in that same pass into an i64 slot the
/// compound's emission reads (`CountLlvm`). False (nothing emitted, nothing
/// published) for a source that is not one dense axis; the ordinary path
/// then runs.
///
/// T4a (the plan's GatherAtMask): the same pass also GATHERS the compound --
/// the predicate's own load of x[i] stored at the cursor (the count itself)
/// of an n + 1 value pool, i into an n + 1 position pool when the table is
/// built -- and feeds the in-pass chains; published as `GatherLlvm` (pools
/// through `ptr` slots, so a run-time-guarded single pass needs no dominance
/// argument). A run-time extent takes the single pass only while n is under
/// the plan's selectivity cap; otherwise the plain counted pass runs and the
/// compound runs B4's pass itself.
and private tryBindCountedMask (c: Ctx) (id: IRId) (name: string) (plan: Blade.Optimize.CompactionPlan) (arrExpr: IRExpr) (predExpr: IRExpr) : bool =
    plan.CountLlvm.Value <- None
    plan.GatherLlvm.Value <- None
    let folds = Blade.Optimize.CompactionTable.foldsOf plan.CompoundId
    for f in folds do f.EmittedLlvm.Value <- None
    // T4c: the shared selection's handshake, and every companion's chains
    plan.Shared |> Option.iter (fun s ->
        s.BuiltLlvm.Value <- None
        for (cid, _, _) in s.Members do
            for f in Blade.Optimize.CompactionTable.foldsOf cid do f.EmittedLlvm.Value <- None)
    match resolveCallable predExpr with
    | Some cl when cl.Params.Length = 1 ->
        let a = consumeArrOperand c arrExpr
        (match a.Groups with
         | [ GDense _ ] | [ GDynDense _ ] ->
             let n = soleExtentOp a
             let pool = allocPool c ScBool n
             let cnt = allocaOf c "i64"
             ln c ($"store i64 0, ptr {cnt}")
             // One source cell of the mask pass: the predicate over x[i], the
             // bit stored; the value and the bit handed back.
             let maskCell (i: string) : Val * string =
                 let v = readCell c a [ { Reg = i; Ty = ScI64 } ]
                 let bit = coerce c ScBool (applyKernel c cl [ KScalar v ])
                 storeCell c ScBool (gepCell c ScBool pool i) bit
                 let w = freshReg c
                 ln c ($"{w} = zext i1 {bit.Reg} to i64")
                 (v, w)
             let advance (w: string) =
                 let cur = loadSlot c cnt ScI64
                 let next = i64Add c cur.Reg w
                 ln c ($"store i64 {next}, ptr {cnt}")
             let countedPass () = emitCountedLoopTo c n (fun i -> let (_, w) = maskCell i in advance w)
             // T4c + T4a: the shared selection's ONE pass is this mask pass
             // (every companion's source cell gathered at the one cursor; the
             // shared positions when demanded; each companion's chains fed).
             // A source this lane does not hold as a stored pool of the
             // mask's shape: the plain counted pass (the first companion then
             // runs the joint pass itself).
             let sharedSrcs =
                 match plan.Shared with
                 // a STATIC extent only: the run-time-guarded shared gather is
                 // not built in this lane (unverified -- see emitSharedCompanion)
                 | Some s when s.GatherAtMask && s.StaticN.IsSome ->
                     let maskSrc = match arrExpr with IRVar (sid, _) -> Some sid | _ -> None
                     let srcs =
                         s.Members |> List.map (fun (cid, _, src) ->
                             let elem = scalarTyOf s.ElemTypes.[cid]
                             let av =
                                 if Some src = maskSrc then Some a
                                 else
                                     match c.ArrSlots.TryGetValue src with
                                     | true, v when (match v.Src with APool _ -> true | _ -> false) && v.Groups = a.Groups -> Some v
                                     | _ -> None
                             (cid, elem, av))
                     let usable =
                         srcs |> List.forall (fun (_, e, av) ->
                             match e, av with
                             | Some (ScI64 | ScF64 | ScBool as el), Some v -> v.Elem = el
                             | _ -> false)
                     if usable then Some (s, srcs |> List.map (fun (cid, e, av) -> (cid, e.Value, av.Value))) else None
                 | _ -> None
             let emitShared (s: Blade.Optimize.SharedSelection, srcs: (IRId * Sc * ArrVal) list) =
                 let dstSlots = srcs |> List.map (fun (cid, _, _) -> (cid, allocaOf c "ptr"))
                 for (_, slot) in dstSlots do ln c ($"store ptr null, ptr {slot}")
                 let posSlot = if s.PositionsNeeded then Some (allocaOf c "ptr") else None
                 posSlot |> Option.iter (fun sl -> ln c ($"store ptr null, ptr {sl}"))
                 let feeds =
                     srcs |> List.collect (fun (cid, elem, _) ->
                         if elem <> ScF64 then []
                         else compactionFeedsLlvm c (Blade.Optimize.CompactionTable.foldsOf cid) |> List.map (fun (f, k, acc) -> (f, k, acc, cid)))
                 let singlePass () =
                     let nPlus = i64Add c n "1"
                     let dsts =
                         srcs |> List.map (fun (cid, elem, _) ->
                             let d = allocPool c elem nPlus
                             ln c ($"store ptr {d}, ptr {(dstSlots |> List.find (fun (k, _) -> k = cid) |> snd)}")
                             (cid, d))
                     let dstOf (cid: IRId) = dsts |> List.find (fun (k, _) -> k = cid) |> snd
                     let pos =
                         posSlot |> Option.map (fun sl ->
                             let p = allocPool c ScI64 nPlus
                             ln c ($"store ptr {p}, ptr {sl}")
                             p)
                     emitCompactionBlocksLlvmMulti c n cnt (feeds |> List.map (fun (_, k, acc, cid) -> (k, acc, dstOf cid))) (fun lo trips ->
                         emitCountedLoopTo c trips (fun k ->
                             let i = i64Add c lo k
                             let v = readCell c a [ { Reg = i; Ty = ScI64 } ]
                             let bit = coerce c ScBool (applyKernel c cl [ KScalar v ])
                             storeCell c ScBool (gepCell c ScBool pool i) bit)
                         emitCountedLoopTo c trips (fun k ->
                             let i = i64Add c lo k
                             let bit = loadCell c ScBool (gepCell c ScBool pool i)
                             let w = freshReg c
                             ln c ($"{w} = zext i1 {bit.Reg} to i64")
                             let q = loadSlot c cnt ScI64
                             for (cid, elem, src) in srcs do
                                 let v = readCell c src [ { Reg = i; Ty = ScI64 } ]
                                 storeCell c elem (gepCell c elem (dstOf cid) q.Reg) (coerce c elem v)
                             pos |> Option.iter (fun p -> storeCell c ScI64 (gepCell c ScI64 p q.Reg) { Reg = i; Ty = ScI64 })
                             advance w))
                 let guard =
                     match s.StaticN with
                     | Some _ ->
                         singlePass ()
                         None
                     | None ->
                         let g = freshReg c
                         ln c (renderCmp { Dest = g; Kind = "icmp"; Pred = "ult"; Ty = ScI64; Lhs = n; Rhs = string s.SelCapCells })
                         let lOne = freshLbl c "csel.one"
                         let lTwo = freshLbl c "csel.two"
                         let lJoin = freshLbl c "csel.join"
                         ln c (renderBr { Cond = g; IfTrue = lOne; IfFalse = lTwo })
                         lbl c lOne
                         singlePass ()
                         ln c (sprintf "br label %%%s" lJoin)
                         lbl c lTwo
                         countedPass ()
                         ln c (sprintf "br label %%%s" lJoin)
                         lbl c lJoin
                         Some g
                 for (f, _, acc, _) in feeds do f.EmittedLlvm.Value <- Some acc
                 ln c ($"; compaction fusion v2, shared selection (T4c + T4a): `{name}`'s pass gathered its {srcs.Length} companions")
                 s.BuiltLlvm.Value <-
                     Some { Blade.Optimize.SharedSelectionLlvm.DstSlots = Map.ofList dstSlots
                            Blade.Optimize.SharedSelectionLlvm.PosSlot = posSlot
                            Blade.Optimize.SharedSelectionLlvm.CountSlot = cnt
                            Blade.Optimize.SharedSelectionLlvm.Guard = guard
                            Blade.Optimize.SharedSelectionLlvm.Feeds = feeds |> List.map (fun (f, _, acc, cid) -> (f.MapId, acc, cid))
                            Blade.Optimize.SharedSelectionLlvm.AtMask = true
                            Blade.Optimize.SharedSelectionLlvm.Ready = false }
             let gatherElem =
                 match a.Elem with
                 | ScI64 | ScF64 | ScBool when plan.GatherAtMask && plan.Shared.IsNone -> Some a.Elem
                 | _ -> None
             match gatherElem with
             | _ when sharedSrcs.IsSome -> emitShared sharedSrcs.Value
             | None -> countedPass ()
             | Some elem ->
                 let dstSlot = allocaOf c "ptr"
                 let posSlot = if plan.PositionsNeeded then Some (allocaOf c "ptr") else None
                 ln c ($"store ptr null, ptr {dstSlot}")
                 posSlot |> Option.iter (fun s -> ln c ($"store ptr null, ptr {s}"))
                 let feeds = if elem = ScF64 then compactionFeedsLlvm c folds else []
                 let singlePass () =
                     let nPlus = i64Add c n "1"
                     let dst = allocPool c elem nPlus
                     ln c ($"store ptr {dst}, ptr {dstSlot}")
                     let pos =
                         posSlot |> Option.map (fun s ->
                             let p = allocPool c ScI64 nPlus
                             ln c ($"store ptr {p}, ptr {s}")
                             p)
                     // Per 256-cell block: the predicate over the block (the
                     // vectorizable loop the counted pass is), then the
                     // branchless gather over the same, L1-resident, block.
                     emitCompactionBlocksLlvm c n dst cnt (feeds |> List.map (fun (_, k, acc) -> (k, acc))) (fun lo trips ->
                         emitCountedLoopTo c trips (fun k ->
                             let i = i64Add c lo k
                             let v = readCell c a [ { Reg = i; Ty = ScI64 } ]
                             let bit = coerce c ScBool (applyKernel c cl [ KScalar v ])
                             storeCell c ScBool (gepCell c ScBool pool i) bit)
                         emitCountedLoopTo c trips (fun k ->
                             let i = i64Add c lo k
                             let v = readCell c a [ { Reg = i; Ty = ScI64 } ]
                             let bit = loadCell c ScBool (gepCell c ScBool pool i)
                             let w = freshReg c
                             ln c ($"{w} = zext i1 {bit.Reg} to i64")
                             let q = loadSlot c cnt ScI64
                             storeCell c elem (gepCell c elem dst q.Reg) (coerce c elem v)
                             pos |> Option.iter (fun p -> storeCell c ScI64 (gepCell c ScI64 p q.Reg) { Reg = i; Ty = ScI64 })
                             advance w))
                 let guard =
                     match plan.StaticN with
                     | Some _ ->
                         singlePass ()
                         None
                     | None ->
                         // the selectivity cap (BLADE_TILE_TUNE sel_cap) as n < cells: no product can overflow
                         let g = freshReg c
                         ln c (renderCmp { Dest = g; Kind = "icmp"; Pred = "ult"; Ty = ScI64; Lhs = n; Rhs = string plan.SelCapCells })
                         let lOne = freshLbl c "cgather.one"
                         let lTwo = freshLbl c "cgather.two"
                         let lJoin = freshLbl c "cgather.join"
                         ln c (renderBr { Cond = g; IfTrue = lOne; IfFalse = lTwo })
                         lbl c lOne
                         singlePass ()
                         ln c (sprintf "br label %%%s" lJoin)
                         lbl c lTwo
                         countedPass ()
                         ln c (sprintf "br label %%%s" lJoin)
                         lbl c lJoin
                         Some g
                 for (f, _, acc) in feeds do f.EmittedLlvm.Value <- Some acc
                 plan.GatherLlvm.Value <-
                     Some { Blade.Optimize.CompactionGatherLlvm.DstSlot = dstSlot; Blade.Optimize.CompactionGatherLlvm.PosSlot = posSlot; Blade.Optimize.CompactionGatherLlvm.CountSlot = cnt
                            Blade.Optimize.CompactionGatherLlvm.Guard = guard; Blade.Optimize.CompactionGatherLlvm.Feeds = feeds |> List.map (fun (f, _, acc) -> (f.MapId, acc)) }
             bindArray c id name { Elem = ScBool; Groups = a.Groups; Src = APool pool; RowOpBytes = 0L }
             plan.CountLlvm.Value <- Some cnt
             true
         | _ -> refuse "mask over compact, ragged, or domain storage")
    | _ -> false

/// The hot-row PRODUCER (plan M13; the C++ lane's RowEpilogue twin): the
/// dense producer is filled one leading-axis slice at a time -- the same
/// cells, in the same storage order, from the same per-cell closure the
/// ordinary materialize runs -- and right after each slice the consumer's
/// row map (an ordinary virtual row map over the producer's POOL, so its
/// kernel reads the slice just written) is evaluated at that row and stored
/// into a rank-1 row-sum pool bound to the plan's RowSumId. The slot for the
/// producer is bound BEFORE the fill so the row map resolves its operand.
/// False when the shape is not a dense rank >= 2 producer: the caller then
/// takes the ordinary path and the consumer keeps the two-pass fold.
and private tryBindHotRowProducer (c: Ctx) (id: IRId) (name: string) (plan: Blade.Optimize.HotRowPlan) (pinfo: ApplyInfo) : bool =
    let record (outcome: Blade.Effects.DecisionOutcome) (evidence: string list) =
        if Blade.Effects.Decisions.active () then
            Blade.Effects.Decisions.record
                { Rule = "hot-row-fold"; Version = 1; Span = c.DeclSpan
                  Subject = plan.ConsumerName
                  Outcome = outcome; Evidence = evidence }
    let a = applyToArr c pinfo
    match a.Src with
    | (AVirt _ | AVirtFlat _) when isDenseShape a.Groups && List.length a.Extents >= 2 ->
        let total = shapeCells a.Groups
        let ptr = allocPool c a.Elem (string total)
        let pooled = { a with Src = APool ptr }
        bindArray c id name pooled
        let cons = emitArr c (IRApplyCombinator plan.RowMap)
        let accTy =
            match resolveCallable plan.FoldKernel |> Option.bind (fun cl -> scalarTyOf cl.RetType) with
            | Some t when t <> ScVoid -> t
            | _ -> cons.Elem
        let rows = List.head a.Extents
        let inner = List.tail a.Extents
        let rsPtr = allocPool c accTy (string rows)
        // B2r (round-2 cell epilogues): `prodsum(row, row)` in a build that
        // keeps the serial association is emitProdSum's ordered chain from
        // +0 over the finished row; the same chain (`fmul` of the cell by
        // itself, `fadd` into the accumulator, the same flags) advances on
        // each cell right behind its store, and the row's value is the chain.
        let cepRecord (outcome: Blade.Effects.DecisionOutcome) (evidence: string list) =
            if Blade.Effects.Decisions.active () then
                Blade.Effects.Decisions.record
                    { Rule = "cell-epilogue"; Version = 1; Span = c.DeclSpan
                      Subject = plan.ConsumerName; Outcome = outcome; Evidence = evidence }
        let cellChain =
            match plan.CellChain with
            | Some Blade.Optimize.Square ->
                let why =
                    if Blade.CodeGenState.fpReassocEnabled () then
                        Some (Blade.Optimize.cellEpilogueLicensedRowDecline "the hot row")
                    elif List.length inner <> 1 then Some "the producer is not one cell loop per row"
                    elif a.Elem <> ScF64 || accTy <> ScF64 then Some "the row or the fold is not Float64 (v1)"
                    else None
                match why with
                | Some w ->
                    cepRecord (Blade.Effects.Declined ("llvm lane: " + w))
                        [ $"the hot-row fold of `{plan.ProducerName}` runs its row kernel on each finished row" ]
                    false
                | None -> true
            | _ -> false
        let chainAcc = if cellChain then Some (allocaOf c "double") else None
        emitCountedLoop c rows (fun i ->
            let iv = { Reg = i; Ty = ScI64 }
            (match chainAcc with
             | Some acc -> ln c ($"store double {(f64Const 0.0)}, ptr {acc}")
             | None -> ())
            emitNest c inner (fun rest ->
                let idxs = iv :: rest
                let v = coerce c a.Elem (readCell c a idxs)
                storeCell c a.Elem (gepCell c a.Elem ptr (storageOffset c a.Groups idxs)) v
                match chainAcc with
                | Some acc ->
                    let prod = freshReg c
                    ln c (renderBin { Dest = prod; Opcode = "fmul"; Flags = fmfFor c ScF64; Ty = ScF64; Lhs = v.Reg; Rhs = v.Reg })
                    let accFlags = withFoldFmf c (Blade.CodeGenState.fpReassocEnabled ()) (fun () -> fmfFor c ScF64)
                    let cur = loadSlot c acc ScF64
                    let s = freshReg c
                    ln c (renderBin { Dest = s; Opcode = "fadd"; Flags = accFlags; Ty = ScF64; Lhs = cur.Reg; Rhs = prod })
                    ln c ($"store double {s}, ptr {acc}")
                | None -> ())
            let rv =
                match chainAcc with
                | Some acc -> loadSlot c acc ScF64
                | None -> coerce c accTy (readCell c cons [ iv ])
            storeCell c accTy (gepCell c accTy rsPtr i) rv)
        if cellChain then
            cepRecord Blade.Effects.Applied
                [ $"llvm lane: row chain interleave: each row's prodsum(row, row) chain advances on each cell of `{plan.ProducerName}` right behind its store (same ops, same flags, same +0)" ]
        c.ArrSlots.[plan.RowSumId] <- { Elem = accTy; Groups = [ GDense rows ]; Src = APool rsPtr; RowOpBytes = 0L }
        true
    | _ ->
        record (Blade.Effects.Declined "the llvm lane keeps the two-pass form: the producer is not a dense rank >= 2 pool it fills row by row")
            [ $"row fold of `{plan.ProducerName}`" ]
        if plan.CellChain.IsSome && Blade.Effects.Decisions.active () then
            Blade.Effects.Decisions.record
                { Rule = "cell-epilogue"; Version = 1; Span = c.DeclSpan
                  Subject = plan.ConsumerName
                  Outcome = Blade.Effects.Declined "llvm lane: the hot-row schedule itself was not taken (the producer is not a dense rank >= 2 pool it fills row by row)"
                  Evidence = [ $"the hot-row fold of `{plan.ProducerName}` runs its row kernel on each finished row" ] }
        false

/// B1 (round-2 cell epilogues) in the LLVM lane: the rank-1 flat producer
/// with its consumer's chain fed behind the stores. False = the ordinary
/// materialize and the two-pass fold (recorded).
and private tryBindFlatEpilogueProducer (c: Ctx) (id: IRId) (name: string) (plan: Blade.Optimize.HotRowPlan) (pinfo: ApplyInfo) : bool =
    // ONE fused loop (this lane is serial): each cell produced, stored, and
    // folded into the consumer's accumulator -- the SAME ops the two-pass
    // consumer (emitReduceCompute's one-level nest) runs, `F(acc, g(cell))`
    // from the same seed, in the same order, with the same flags
    // (foldFmfDecorable: reassoc only on a licensed builtin combine). The
    // cell's read-back is a load of the value just stored at the same
    // address, which GVN forwards. Under the licence the vectorizer then
    // folds the stream in its own lane tree, as it does the two-pass loop
    // (within the lanes' usual tolerance); without it the chain is the
    // ordered one, bit for bit. The seed and the kernel are evaluated
    // ahead of the producer, so both must be unable to fail: a literal seed
    // and a builtin combine. Anything else declines (recorded) and the
    // consumer keeps the two-pass fold.
    let record (outcome: Blade.Effects.DecisionOutcome) (evidence: string list) =
        if Blade.Effects.Decisions.active () then
            Blade.Effects.Decisions.record
                { Rule = "cell-epilogue"; Version = 1; Span = c.DeclSpan
                  Subject = plan.ConsumerName
                  Outcome = outcome; Evidence = evidence }
    let declined (why: string) =
        record (Blade.Effects.Declined ("llvm lane: " + why))
            [ $"`{plan.ConsumerName}` folds the cells of `{plan.ProducerName}` after the producer has stored them all (two passes)" ]
        false
    match plan.Seed, resolveCallable plan.FoldKernel with
    | IRLit _, Some cl when cl.Params.Length = 2 && (Blade.CodeGenExprSupport.foldKernelBuiltinOp cl).IsSome ->
        let a = applyToArr c pinfo
        match a.Src with
        | (AVirt _ | AVirtFlat _) when isDenseShape a.Groups && List.length a.Extents = 1 ->
            let total = shapeCells a.Groups
            let ptr = allocPool c a.Elem (string total)
            let pooled = { a with Src = APool ptr }
            bindArray c id name pooled
            let cons = emitArr c (IRApplyCombinator plan.RowMap)
            let ty =
                match scalarTyOf cl.RetType with
                | Some t when t <> ScVoid -> t
                | _ -> cons.Elem
            let slot = allocaOf c (llTy ty)
            let seed = coerce c ty (emitExpr c plan.Seed)
            ln c ($"store {(llTy ty)} {seed.Reg}, ptr {slot}")
            let licensed = foldFmfDecorable c cl
            // A7c (round 3, gap G69): a planned join's producer-INPUT legs
            // ride the same loop -- each its own chain from its own seed
            // (evaluated once, here, before the loop; the admission proved it
            // total and bound before the producer), fed by the operand cell
            // the producer already loads (GVN forwards the second load), with
            // the leg's OWN fold flags (foldFmfDecorable: reassoc only on the
            // leg's own licence). Bit for bit the joined pass's chain when
            // strict; the vectorizer's tree when licensed, as in the joined
            // pass. The join binds its tuple from the published slots.
            let legPlan =
                match Blade.Optimize.EpilogueLegTable.tryProducer id with
                | Some lp ->
                    let resolved =
                        lp.Legs |> List.map (fun l ->
                            match resolveCallable l.Kernel, List.tryItem l.OperandPos pinfo.Arrays with
                            | Some lcl, Some opExpr when lcl.Params.Length = 2 -> Some (l, lcl, opExpr)
                            | _ -> None)
                    if resolved |> List.forall Option.isSome then Some (lp, resolved |> List.map Option.get)
                    else
                        lp.DecidedLlvm.Value <- true
                        if Blade.Effects.Decisions.active () then
                            Blade.Effects.Decisions.record
                                { Rule = "epilogue-legs"; Version = 1; Span = c.DeclSpan; Subject = lp.Subject
                                  Outcome = Blade.Effects.Declined "llvm lane: a leg's fold kernel or operand does not resolve"
                                  Evidence = [ "the legs keep their joined pass" ] }
                        None
                | None -> None
            let legState =
                match legPlan with
                | Some (lp, legs) ->
                    legs |> List.map (fun (l, lcl, opExpr) ->
                        let lty =
                            match scalarTyOf lcl.RetType with
                            | Some t when t <> ScVoid -> t
                            | _ -> refuse "an epilogue leg whose fold kernel has no scalar result"
                        let lslot = allocaOf c (llTy lty)
                        let lseed = coerce c lty (emitExpr c l.Seed)
                        ln c ($"store {(llTy lty)} {lseed.Reg}, ptr {lslot}")
                        (l, lcl, emitArr c opExpr, lslot, lty, foldFmfDecorable c lcl))
                | None -> []
            emitCountedLoop c total (fun i ->
                let iv = { Reg = i; Ty = ScI64 }
                let v = coerce c a.Elem (readCell c a [ iv ])
                storeCell c a.Elem (gepCell c a.Elem ptr (storageOffset c a.Groups [ iv ])) v
                let cur = loadSlot c slot ty
                let cell = readCell c cons [ iv ]
                let next =
                    withFoldFmf c licensed (fun () ->
                        coerce c ty (applyKernel c cl [ KScalar cur; KScalar cell ]))
                ln c ($"store {(llTy ty)} {next.Reg}, ptr {slot}")
                for (_, lcl, opArr, lslot, lty, llic) in legState do
                    let lcur = loadSlot c lslot lty
                    let lcell = readCell c opArr [ iv ]
                    let lnext =
                        withFoldFmf c llic (fun () ->
                            coerce c lty (applyKernel c lcl [ KScalar lcur; KScalar lcell ]))
                    ln c ($"store {(llTy lty)} {lnext.Reg}, ptr {lslot}"))
            c.Slots.[plan.RowSumId] <- (slot, ty)
            (match legPlan with
             | Some (lp, _) ->
                for (l, _, _, lslot, lty, _) in legState do
                    c.Slots.[l.SlotId] <- (lslot, lty)
                lp.DecidedLlvm.Value <- true
                let forms =
                    legState |> List.map (fun (l, _, _, _, _, llic) ->
                        if llic then $"{l.Name}: builtin-licensed (its own licence: `reassoc nsz` on its own adds; the vectorizer may pick a different tree than in the joined pass -- a licensed association change)"
                        else $"{l.Name}: strict ascending chain from its own seed")
                let legNames = lp.Legs |> List.map (fun l -> l.Name) |> String.concat ", "
                if Blade.Effects.Decisions.active () then
                    Blade.Effects.Decisions.record
                        { Rule = "epilogue-legs"; Version = 1; Span = c.DeclSpan; Subject = lp.Subject
                          Outcome = Blade.Effects.Applied
                          Evidence = [ $"llvm lane: legs {legNames} folded inside `{lp.ProducerName}`'s one fused loop; the joined pass over the operands is gone" ] @ forms }
             | None -> ())
            record Blade.Effects.Applied
                [ $"""llvm lane: rank-1 producer: one fused loop -- each cell of `{plan.ProducerName}` produced, stored and folded into `{plan.ConsumerName}` ({(if licensed then "licensed: the vectorizer's reduction tree over the stream" else "the ordered chain")})""" ]
            true
        | _ -> declined "the producer is not a dense rank-1 pool it fills cell by cell"
    | IRLit _, _ -> declined "the fold kernel is not a builtin `+` / `*` (evaluated inside the producer, a failure could move)"
    | _ -> declined "the fold's seed is not a literal (evaluated ahead of the producer, a failure could move)"

/// The hot-row CONSUMER: the outer combine over the row-sum pool -- the same
/// seeded chain emitReduceCompute's one-level nest runs over the deferred
/// row map (seed, then `kernel(acc, cell)` per row ascending, the licensed
/// fold flags on the kernel application only), now over stored values.
and private emitHotRowConsumer (c: Ctx) (id: IRId) (name: string) (declTy: IRType)
                               (plan: Blade.Optimize.HotRowPlan) (kernelExpr: IRExpr) (seedExpr: IRExpr) : unit =
    let a = c.ArrSlots.[plan.RowSumId]
    let cl =
        match resolveCallable kernelExpr with
        | Some cl when cl.Params.Length = 2 -> cl
        | _ -> refuse "a hot-row fold whose kernel does not resolve to a two-parameter callable"
    let ty =
        match scalarTyOf cl.RetType with
        | Some t when t <> ScVoid -> t
        | _ -> a.Elem
    let rows = match a.Groups with [ GDense n ] -> n | _ -> refuse "a hot-row row-sum pool that is not rank-1 dense"
    let slot = allocaOf c (llTy ty)
    let seed = coerce c ty (emitExpr c seedExpr)
    ln c ($"store {(llTy ty)} {seed.Reg}, ptr {slot}")
    let licensed = foldFmfDecorable c cl
    emitCountedLoop c rows (fun i ->
        let cur = loadSlot c slot ty
        let cell = readCell c a [ { Reg = i; Ty = ScI64 } ]
        let next =
            withFoldFmf c licensed (fun () ->
                coerce c ty (applyKernel c cl [ KScalar cur; KScalar cell ]))
        ln c ($"store {(llTy ty)} {next.Reg}, ptr {slot}"))
    let v = loadSlot c slot ty
    let target = match scalarTyOf declTy with Some t when t <> ScVoid -> t | _ -> v.Ty
    bindScalar c id name (coerce c target v)

and private copyArr (c: Ctx) (a: ArrVal) : ArrVal =
    match a.Src with
    | AVirt _ | AVirtFlat _ -> a
    | APool src ->
        let total = shapeCellsOp c a.Groups
        let dst = allocPool c a.Elem total
        emitCountedLoopTo c total (fun i ->
            let v = loadCell c a.Elem (gepCell c a.Elem src i)
            storeCell c a.Elem (gepCell c a.Elem dst i) v)
        { a with Src = APool dst }

/// What a value expression produced. The split exists because an LLVM value is
/// one register, a Blade value may be a pool, a producer or a flat pack -- and
/// the difference is decided by the expression, not the type (a fused
/// reduction join is `IRTTuple`-typed only after the checker ran).
and private classifyValue (c: Ctx) (e: IRExpr) : ValKind =
    match e with
    | IRMethodFor _ | IRObjectFor _ | IRComposeObj _ | IRCompose _ -> VOpaque
    // `let f = lambda(x, y) -> ...`: the lambda already lifted to a module
    // function and the binding is an ALIAS for it (IRPrint's alias table is
    // what every kernel slot resolves through). Nothing to emit.
    | IRVar (id, _) when c.Callables.ContainsKey id && not (c.ArrSlots.ContainsKey id) && not (c.Slots.ContainsKey id) -> VOpaque
    | IRReduceCompute (comp, k, i) ->
        (match emitReduceCompute c comp k i with
         | [ v ] -> VScalar v
         | vs -> VTuple (vs |> List.map VScalar))
    | IRParallel _ | IRFusion _ -> VTuple (emitTupleParts c e)
    | IRCompute (IRParallel _ | IRFusion _ | IRTuple _) -> VTuple (emitTupleParts c e)
    | IRTuple _ -> VTuple (emitTupleParts c e)
    // A BARE range in value position (`let xs = 0..n`, its `|> compute`
    // form, the lift pass's hoisted reduce operand): an ARRAY value, though
    // `typeOf` answers the IntValued tier's Int64 scalar (the element under
    // nest peeling), so the type fall-through below would misroute it to the
    // scalar lane's refusal. emitArr already carries the descriptor;
    // materialize it (rather than leave it AVirt) to mirror the C++ lane's
    // genRangeBinding -- every Blade binding except `static` is assignable,
    // and a write needs a pool. Non-plain/multi-slot ranges still refuse
    // inside emitArr/staticExtentOf, keeping the lane's clean-skip contract.
    | IRRange _ -> VArray (materialize c (emitArr c e))
    | IRCompute (IRRange _ as inner) -> VArray (materialize c (emitArr c inner))
    | _ ->
        match Blade.IR.stripUnits (typeOf e) with
        | ArrayElem _ -> VArray (emitArr c e)
        // A `Computation<T>` over a SCALAR (`pure(42)`) carries no shape: the
        // monad wrapper is erased and the value is an ordinary register.
        | IRTComputation inner when (scalarTyOf inner).IsSome -> VScalar (emitExpr c e)
        | IRTComputation _ -> VArray (emitArr c e)
        | IRTTuple _ -> VTuple (emitTupleParts c e)
        | _ -> VScalar (emitExpr c e)

// ---------------------------------------------------------------------------
// Array-valued expressions
// ---------------------------------------------------------------------------

and private emitArr (c: Ctx) (e: IRExpr) : ArrVal =
    spineStatement c e |> ignore
    match e with
    | IRVar (id, _) ->
        (match c.ArrSlots.TryGetValue id with
         | true, a -> a
         | _ ->
             if c.LoopObjs.Contains id then
                 refuse "a loop object (method_for/object_for) used where an array is required"
             else refuse ($"reference to an unbound array (ir id {id})"))

    | IRParam (name, _, _) ->
        (match c.NameArrSlots.TryGetValue name with
         | true, a -> a
         | _ -> refuse ($"reference to array parameter '{name}' outside a function body"))

    | IRArrayLit (elems, arrType) -> emitArrayLit c elems arrType

    // The let-rec buffer whose zero fill the desugar proved dead (IR.IRUninit,
    // formalism 7.5): a tracked pool, allocated and NOT filled --
    // blade_alloc_cells returns uninitialized storage, and every cell is
    // written by the recursion before any read. Dense shapes only, as the
    // desugar admits.
    | IRUninit ty ->
        let (elem, groups) = requireArray "an unfilled recursive-array buffer" ty
        if not (isDenseShape groups) then refuse "an unfilled recursive-array buffer over a non-dense shape"
        let ptr = allocPool c elem (string (shapeCells groups))
        { Elem = elem; Groups = groups; Src = APool ptr; RowOpBytes = 0L }

    // `range<I>` / `reverse<I>` as a value in their own right. Inside a
    // combinator they never become one -- the nest peels them as induction
    // values (VirtualKind), which is the whole point of a virtual array.
    | IRRange (ixs, offset) ->
        let extents = ixs |> List.map (fun ix -> staticExtentOf ix "range")
        let off =
            match offset with
            | Some o -> coerce c ScI64 (emitExpr c o)
            | None -> { Reg = "0"; Ty = ScI64 }
        { Elem = ScI64; Groups = denseGroups extents
          RowOpBytes = 0L
          Src = AVirt (fun c2 idxs ->
                    match idxs with
                    | [ i ] -> addI64 c2 i off
                    | _ -> refuse "a multi-slot range read outside a loop nest") }

    | IRVirtualReverse ix ->
        let n = staticExtentOf ix "reverse"
        { Elem = ScI64; Groups = [ GDense n ]
          RowOpBytes = 0L
          Src = AVirt (fun c2 idxs ->
                    match idxs with
                    | [ i ] ->
                        let d = freshReg c2
                        ln c2 (renderBin { Dest = d; Opcode = "sub"; Flags = ""; Ty = ScI64
                                           Lhs = string (n - 1L); Rhs = i.Reg })
                        { Reg = d; Ty = ScI64 }
                    | _ -> refuse "a multi-slot reverse read outside a loop nest") }

    // `|> compute` IS the materialization site, and the only one: everything
    // else in this file stays a producer.
    // `|> compute` of a TWO-STAGE PIPELINE (`object_for(f) >>@ object_for(g)`
    // applied, then forced): the C++ lane runs two complete sweeps with a
    // stage-typed intermediate (CodeGenFusion's staged path), and nesting a
    // forced stage-one application inside the stage-two application is that
    // order exactly -- objectForApply emits its operand once, and the inner
    // IRCompute finishes stage one before stage two reads it. Conservative:
    // unary scalar stages over a stored, static dense input; everything else
    // (bare deferred pipelines, row kernels, several inputs, nested chains)
    // refuses. Drafted by Codex.
    | IRCompute (IRComposeApply info) ->
        let rec resolve (seen: Set<IRId>) (expr: IRExpr) =
            match expr with
            | IRVar (id, _) when not (Set.contains id seen) ->
                (match Map.tryFind id c.BindingValues with
                 | Some value -> resolve (Set.add id seen) value
                 | None -> expr)
            | _ -> expr
        let stage (expr: IRExpr) =
            let obj =
                match resolve Set.empty expr with
                | IRObjectFor obj when obj.InputRanks = [ 0 ] && obj.OutputRank = 0 -> obj
                | _ -> refuse "a staged pipeline leaf other than a unary scalar object_for"
            let cl =
                match resolveCallable obj.Kernel with
                | Some cl -> cl
                | None -> refuse "an unresolved staged pipeline kernel"
            if cl.IsArityPoly || cl.IsCudaKernel || cl.IsMpiParallel then
                refuse "a staged pipeline kernel with unsupported polymorphism or strategy"
            let paramTy =
                match cl.Params with
                | [ p ] -> p.Type
                | _ -> refuse "a staged pipeline kernel with non-unary parameters"
            let retTy =
                match cl.RetType with
                | IRTInfer _ -> typeOf cl.Body
                | t -> t
            let scalar (what: string) (ty: IRType) =
                match scalarTyOf ty with
                | Some sc when sc = ScI64 || sc = ScF64 || sc = ScBool -> sc
                | _ -> refuse ($"a staged pipeline {what} outside Int64/Float64/Bool")
            (obj, retTy, scalar "parameter" paramTy, scalar "result" retTy)
        let (left, right) =
            match resolve Set.empty info.Composition with
            | IRComposeObj (l, r) -> (l, r)
            | _ -> refuse "a staged pipeline that does not resolve to two object stages"
        let (o1, t1, p1, r1) = stage left
        let (o2, _, p2, r2) = stage right
        let src =
            match info.InputArrays with
            | [ (IRVar _ | IRParam _) as src ] -> src
            | [ _ ] -> refuse "a staged pipeline over an anonymous input"
            | _ -> refuse "a staged pipeline over multiple inputs"
        let srcType =
            match Blade.IR.stripUnits (typeOf src) with
            | ArrayElem at -> at
            | _ -> refuse "a staged pipeline over a non-array input"
        let a = emitArr c src
        (match a.Src with
         | APool _ -> ()
         | _ -> refuse "a staged pipeline over a deferred input")
        if not (a.Groups |> List.forall (function GDense _ -> true | _ -> false)) then
            refuse "a staged pipeline over compact, ragged, or runtime storage"
        let (outElem, outGroups) = requireArray "a staged pipeline result" info.OutputType
        if p1 <> a.Elem || p2 <> r1 || r2 <> outElem || outGroups <> a.Groups then
            refuse "a staged pipeline with incompatible stage types or shapes"
        let s1Type = mkArrayLike { srcType with ElemType = t1 }
        let first = IRCompute (IRApp (IRObjectFor o1, [ src ], s1Type))
        materialize c (objectForApply c o2 [ first ] info.OutputType)

    | IRCompute inner -> materialize c (emitArr c inner)
    | IRPure inner -> emitArr c inner

    | IRApplyCombinator info -> applyToArr c info
    | IRApp (IRObjectFor objInfo, args, retType) -> objectForApply c objInfo args retType
    | IRApp _ -> emitArrCall c e

    | IRIndex (arrExpr, idxExprs, _) ->
        let a = emitArr c arrExpr
        let idxs = idxExprs |> List.map (fun ix -> coerce c ScI64 (emitExpr c ix))
        if List.length idxs >= a.Rank then
            refuse "a full index where an array was expected"
        rowView c a idxs

    | IRTupleProj (parent, idx, isFlat) ->
        (match tupleComponent c parent idx isFlat with
         | VArray a -> a
         | _ -> refuse ($"tuple projection [{idx}] of a non-array component in array position"))

    | IRLet (id, value, body) ->
        bindLetIn c id value body
        emitArr c body

    | IRSequence exprs ->
        (match List.rev exprs with
         | last :: earlier ->
             for x in List.rev earlier do emitExpr c x |> ignore
             emitArr c last
         | [] -> refuse "an empty sequence in array position")

    | IRBind (comp, cont) -> emitArr c (bindContinuationBody c comp cont)

    // AN ARRAY-VALUED `if`, which is one construct's signature and not a
    // general facility: the guarded prefix read a recursive array desugars to.
    // `prefix(n - 3)` at n < 3 is formalism 7.5's implicit zero, and
    // TypeCheckInfer's `guardWrap` spells it as a select over a CLAMPED row
    // read -- `if n - 3 >= 0 then <the lag row> else <the zero slice>` --
    // because merely FORMING an out-of-range row view is undefined, so the
    // discarded branch has to stay in bounds too.
    //
    // What the select answers is a POINTER, chosen once, rather than a
    // producer that re-tests the condition in every cell of the copy nest that
    // consumes it. Both branches are stored arrays of one shape here (a row
    // view and a zero literal); a branch that is still a producer materializes
    // INSIDE its own block, so nothing escapes the arm that built it.
    | IRIf (cond, tb, fb) ->
        let cv = coerce c ScBool (emitExpr c cond)
        let slot = allocaOf c "ptr"
        let lThen = freshLbl c "arrif.then"
        let lElse = freshLbl c "arrif.else"
        let lEnd = freshLbl c "arrif.end"
        let poolOf (x: ArrVal) =
            match x.Src with
            | APool p -> p
            | _ -> refuse "an unmaterialized array branch of an if-expression"
        ln c (renderBr { Cond = cv.Reg; IfTrue = lThen; IfFalse = lElse })
        lbl c lThen
        let a = materialize c (emitArr c tb)
        ln c ($"store ptr {(poolOf a)}, ptr {slot}")
        ln c (sprintf "br label %%%s" lEnd)
        lbl c lElse
        let b = materialize c (emitArr c fb)
        if b.Elem <> a.Elem || b.Groups <> a.Groups then
            refuse "an if-expression whose branches are arrays of different static shapes"
        ln c ($"store ptr {(poolOf b)}, ptr {slot}")
        ln c (sprintf "br label %%%s" lEnd)
        lbl c lEnd
        let p = freshReg c
        ln c ($"{p} = load ptr, ptr {slot}")
        { a with Src = APool p }

    // Elementwise `<|>`: keep the left cell when it is non-zero, else the
    // right. Value-keyed, not allocation-keyed -- that distinction is what
    // separates `<|>` from `<|:>` (which stays refused).
    | IRChoice (l, r) ->
        let a = emitArr c l
        let b = emitArr c r
        if a.Groups <> b.Groups then refuse "a choice between arrays of different static shapes"
        { a with Src = AVirt (fun c2 idxs ->
                    let lv = readCell c2 a idxs
                    let nz = coerce c2 ScBool lv
                    let slot = allocSlot c2 a.Elem
                    let lKeep = freshLbl c2 "cchoice.keep"
                    let lElse = freshLbl c2 "cchoice.else"
                    let lEnd = freshLbl c2 "cchoice.end"
                    ln c2 (renderBr { Cond = nz.Reg; IfTrue = lKeep; IfFalse = lElse })
                    lbl c2 lKeep
                    storeSlot c2 slot a.Elem lv
                    ln c2 (sprintf "br label %%%s" lEnd)
                    lbl c2 lElse
                    storeSlot c2 slot a.Elem (readCell c2 b idxs)
                    ln c2 (sprintf "br label %%%s" lEnd)
                    lbl c2 lEnd
                    match slot with
                    | Some s -> loadSlot c2 s a.Elem
                    | None -> refuse "a choice over unit cells") }

    // group_by(vals, gk): ONE group-contiguous CSR pool, gathered through the
    // permutation in a single flat loop -- versus the C++ lane's ngroups
    // separate `new T[sz]` blocks plus the same gather. The result is the
    // ragged pair over the gk's offsets table, so every downstream consumer
    // (peel, row view, direct read, row reduce) is the machinery ragged
    // already built; nothing below this arm knows it came from a grouping.
    | IRGroupBy (values, grouping) ->
        let gk = gkOf c grouping
        let vals = consumeArrOperandAnyOrderTotal c values
        (match vals.Groups with
         | [ GDense _ ] -> ()
         | _ -> refuse "group_by over a values operand that is not a dense rank-1 axis")
        let table = RtDynamic gk.Offsets
        let total = raggedOffAt c table gk.NGroups
        let out = allocPool c vals.Elem total
        emitCountedLoopTo c total (fun t ->
            let p = loadCell c ScI64 (gepCell c ScI64 gk.Perm t)
            let v = coerce c vals.Elem (readCell c vals [ p ])
            storeCell c vals.Elem (gepCell c vals.Elem out t) v)
        { Elem = vals.Elem; Groups = [ GRagged (gk.NGroups, table) ]
          Src = APool out; RowOpBytes = 0L }

    // extents(gk): per-group sizes, offsets[g+1] - offsets[g], no gather --
    // a producer, so a sizes-only consumer never touches the data at all
    // (the gather-elision special case, obtained structurally).
    | IRGroupSizes grouping ->
        let gk = gkOf c grouping
        { Elem = ScI64; Groups = [ denseOfOp gk.NGroups ]; RowOpBytes = 0L
          Src = AVirt (fun c2 idxs ->
                    match idxs with
                    | [ g ] -> { Reg = raggedLenAt c2 (RtDynamic gk.Offsets) g.Reg; Ty = ScI64 }
                    | _ -> refuse "a group-sizes read at other than its group coordinate") }

    // group_bucket(gk): invert the CSR -- row -> bucket, -1 for dropped rows.
    // The -1 prefill is explicit (the pool arrives uninitialized, and 0 is
    // a real bucket); the inversion walks groups x members with the row bounds
    // hoisted once per group.
    | IRGroupBucket grouping ->
        let gk = gkOf c grouping
        let out = allocPool c ScI64 (string gk.NSrc)
        emitCountedLoop c gk.NSrc (fun t ->
            storeCell c ScI64 (gepCell c ScI64 out t) { Reg = "-1"; Ty = ScI64 })
        emitCountedLoopTo c gk.NGroups (fun g ->
            let lo = loadCell c ScI64 (gepCell c ScI64 gk.Offsets g)
            let hi = loadCell c ScI64 (gepCell c ScI64 gk.Offsets (i64Bin c "add" g "1"))
            emitCountedLoopTo c (i64Bin c "sub" hi.Reg lo.Reg) (fun j ->
                let t = i64Bin c "add" lo.Reg j
                let row = loadCell c ScI64 (gepCell c ScI64 gk.Perm t)
                storeCell c ScI64 (gepCell c ScI64 out row.Reg) { Reg = g; Ty = ScI64 }))
        { Elem = ScI64; Groups = [ GDense gk.NSrc ]; Src = APool out; RowOpBytes = 0L }

    // negate / conjugate of a whole array: an EAGER copy into fresh,
    // same-shape storage (the C++ lane's negate_pool / conjugate path), so a
    // later write to the source never shows through. Shape-preserving and
    // cell-congruent, so a packed sym/antisym operand is one flat loop.
    // Conjugation of a real element is the identity copy; complex refuses at
    // the element gate.
    | IRArrayNegate arrExpr | IRArrayConjugate arrExpr ->
        let negate = match e with IRArrayNegate _ -> true | _ -> false
        let (elem, groups) = requireArray "an array negate/conjugate" (typeOf e)
        if elem <> ScI64 && elem <> ScF64 then
            refuse "array negate/conjugate with a non-Int64/Float64 element"
        let a = materialize c (consumeArrOperandAnyOrderTotal c arrExpr)
        if a.Elem <> elem || a.Groups <> groups then
            refuse "array negate/conjugate with inconsistent operand storage"
        let total = shapeCellsOp c groups
        let ptr = allocPool c elem total
        emitCountedLoopTo c total (fun k ->
            let v = readFlat c a { Reg = k; Ty = ScI64 }
            let out =
                if not negate then v
                else
                    let r = freshReg c
                    (match elem with
                     | ScF64 -> ln c ($"{r} = fneg double {v.Reg}")
                     | _ -> ln c ($"{r} = sub i64 0, {v.Reg}"))
                    { Reg = r; Ty = elem }
            storeCell c elem (gepCell c elem ptr k) out)
        { a with Src = APool ptr; RowOpBytes = 0L }

    // gram(A, B) = A Bᴴ, eager, the C++ lane's NATIVE contraction: each cell
    // starts at zero and accumulates A[i,k] * conj(B[j,k]) with k ascending
    // innermost (conj is the identity on the real elements this lane has).
    // The C++ lane jams columns without changing any cell's order, so the
    // per-cell fold is the whole contract. Same-array output is the packed
    // triangle i <= j, whose storage coordinate is jr = j - i. Never
    // reassociated (the fold stays ordered); `contract` only under
    // BLADE_FP_CONTRACT, as g++ contracts the C++ lane's `acc += a*b`. A BLAS or
    // cuBLAS route belongs to the C++ lane, so its presence refuses.
    | IRGram (left, right, isSameArray) ->
        if (Blade.LinAlgPatterns.classify e |> Option.bind Blade.LinAlgPatterns.resolveNodeRoute).IsSome then
            refuse "gram routed to BLAS/cuBLAS (the C++ lane owns library routes)"
        let a = consumeArrOperandAnyOrderTotal c left
        let b = consumeArrOperandAnyOrderTotal c right
        let m, n, p =
            match a.Groups, b.Groups with
            | [ GDense m; GDense n ], [ GDense p; GDense k ] when n = k -> m, n, p
            | _ -> refuse "gram requires two static dense rank-2 operands with equal trailing extents"
        let elem, groups = requireArray "a gram result" (typeOf e)
        if (elem <> ScF64 && elem <> ScI64) || a.Elem <> elem || b.Elem <> elem then
            refuse "gram requires matching Int64 or Float64 elements"
        let expected = if isSameArray then [ GSym (2, m) ] else [ GDense m; GDense p ]
        if groups <> expected || (isSameArray && m <> p) then
            refuse "a gram whose checked type disagrees with its operand shape"
        let ptr = allocPool c elem (string (shapeCells groups))
        let acc = allocaOf c (llTy elem)
        let zero = if elem = ScF64 then f64Const 0.0 else "0"
        let mulOp, addOp = if elem = ScF64 then "fmul", "fadd" else "mul", "add"
        emitShapeNest c groups (fun idxs off ->
            let i = idxs.[0]
            let j = if isSameArray then { Reg = i64Add c i.Reg idxs.[1].Reg; Ty = ScI64 } else idxs.[1]
            ln c ($"store {(llTy elem)} {zero}, ptr {acc}")
            emitCountedLoop c n (fun k ->
                let kv = { Reg = k; Ty = ScI64 }
                let av = readCell c a [ i; kv ]
                let bv = readCell c b [ j; kv ]
                let prod = freshReg c
                ln c (renderBin { Dest = prod; Opcode = mulOp; Flags = fmfFor c elem; Ty = elem; Lhs = av.Reg; Rhs = bv.Reg })
                let cur = loadSlot c acc elem
                let sum = freshReg c
                ln c (renderBin { Dest = sum; Opcode = addOp; Flags = fmfFor c elem; Ty = elem; Lhs = cur.Reg; Rhs = prod })
                ln c ($"store {(llTy elem)} {sum}, ptr {acc}"))
            storeCell c elem (gepCell c elem ptr off.Reg) (loadSlot c acc elem))
        { Elem = elem; Groups = groups; Src = APool ptr; RowOpBytes = 0L }

    // transpose(A, d1, d2): a HARD copy into a fresh pool, as the C++ lane's
    // materializer makes it (swapped extents, filled in source order), so a
    // later write to A never shows through and the binding is eager and
    // prints. Dense static axes only: a symmetric within-group swap is
    // erased upstream, an antisymmetric/Hermitian one would need a
    // negate/conjugate copy, and the C++ materializer itself assumes
    // rectangular slots.
    | IRTranspose (arrExpr, d1, d2) ->
        let a = consumeArrOperandAnyOrderTotal c arrExpr
        if not (a.Groups |> List.forall (function GDense _ -> true | _ -> false)) then
            refuse "transpose of a compact, ragged, or runtime-extent array"
        let rank = a.Groups.Length
        if d1 < 0 || d2 < 0 || d1 >= rank || d2 >= rank || d1 = d2 then
            refuse "transpose with invalid or equal axes"
        let swapDim d = if d = d1 then d2 elif d = d2 then d1 else d
        let groups = List.init rank (fun d -> a.Groups.[swapDim d])
        if snd (requireArray "a transpose result" (typeOf e)) <> groups then
            refuse "a transpose whose checked type disagrees with its swapped operand shape"
        let ptr = allocPool c a.Elem (string (shapeCells groups))
        emitNest c a.Extents (fun idxs ->
            let v = readCell c a idxs
            let dstIdxs = List.init rank (fun d -> idxs.[swapDim d])
            storeCell c a.Elem (gepCell c a.Elem ptr (storageOffset c groups dstIdxs)) v)
        { a with Groups = groups; Src = APool ptr; RowOpBytes = 0L }

    | IRZip _ -> refuse "zip(...) outside a method_for (a bare zip has no materialized shape)"

    // mask(A, pred): the Bool PRESENCE array over A's own index space --
    // m[i] = pred(A[i]), same shape, no compaction (that is compound's job).
    // EAGER, as the C++ lane's genMaskBinding (`// mask: count + compact`)
    // makes it: a bound mask prints, and a compound reads it as a pool.
    // Dense static shapes and the two rank-1 operand-extent shapes; a mask
    // over compact/ragged storage refuses (the C++ lane's materializeMaskForm
    // has no form for those either).
    | IRMask (arrExpr, predExpr) ->
        let a = consumeArrOperandAnyOrderTotal c arrExpr
        let cl =
            match resolveCallable predExpr with
            | Some cl when cl.Params.Length = 1 -> cl
            | Some cl -> refuse ($"a mask predicate '{cl.Name}' of {cl.Params.Length} parameters")
            | None -> refuse "a mask whose predicate does not resolve to a callable"
        if not (a.Groups |> List.forall (function GDense _ | GDynDense _ | GCompact _ -> true | _ -> false)) then
            refuse "mask over compact, ragged, or domain storage"
        materialize c
            { Elem = ScBool; Groups = a.Groups; RowOpBytes = 0L
              Src = AVirt (fun c2 idxs ->
                        coerce c2 ScBool (applyKernel c2 cl [ KScalar (readCell c2 a idxs) ])) }

    // decompact(A, d): value-equivalent group FISSION. The logical axes and
    // their order are unchanged -- only the grouping is -- so cell
    // (i1, ..., ik) of the result, in whatever groups the checker gave it, is
    // the operand read at the SAME logical coordinates: canonRead sorts them
    // into the operand's own groups and applies an antisymmetric sign. A
    // producer, like the interpreter's decompactArray (readCompact per output
    // cell); the operand rule forces a deferred input exactly where the C++
    // lane's forceDeferredArrayInput does.
    | IRDecompact (arrExpr, _) ->
        let a = consumeArrOperandAnyOrderTotal c arrExpr
        if hasRagged a.Groups then refuse "decompact of a ragged array"
        let (_, groups) = requireArray "a decompact result" (typeOf e)
        if List.sumBy grpRank groups <> a.Rank then
            refuse "a decompact whose result rank differs from its operand's"
        // A producer is handed STORAGE coordinates: a compact group's k-th
        // coordinate is the step above the previous index's canonical floor
        // (grpOffset), so the logical index is the running sum
        // i_k = i_(k-1) + strict + s_k. canonRead wants logical indices.
        let toLogical (c2: Ctx) (idxs: Val list) : Val list =
            let mutable rest = idxs
            [ for g in groups do
                let r = grpRank g
                let mine = rest |> List.truncate r
                rest <- rest |> List.skip r
                if not (grpCompact g) then yield! mine
                else
                    let sInc = if grpStrict g then "1" else "0"
                    let mutable prev : string option = None
                    for (sk: Val) in mine do
                        let ik =
                            match prev with
                            | None -> sk.Reg
                            | Some p -> i64Add c2 (i64Add c2 p sInc) sk.Reg
                        prev <- Some ik
                        yield { Reg = ik; Ty = ScI64 } ]
        { Elem = a.Elem; Groups = groups; RowOpBytes = 0L
          Src = AVirt (fun c2 idxs -> canonRead c2 a (toLogical c2 idxs)) }

    | other ->
        refuse ($"the IR node {(caseName other)} in array position -- no arm in the llvm lane")

and private addI64 (c: Ctx) (a: Val) (b: Val) : Val =
    if b.Reg = "0" then a
    else
        let d = freshReg c
        ln c (renderBin { Dest = d; Opcode = "add"; Flags = ""; Ty = ScI64; Lhs = a.Reg; Rhs = b.Reg })
        { Reg = d; Ty = ScI64 }

and private staticExtentOf (ix: IRIndexType) (what: string) : int64 =
    if ix.IxKind <> IxKPlain || ix.Symmetry <> SymNone || ix.Rank <> 1 then
        refuse ($"a {what} over a non-dense index type")
    match Blade.IRPrint.tryEvalIntIR ix.Extent with
    | Some n -> n
    | None -> refuse ($"a {what} over a runtime extent")

/// A RAGGED literal. Row lengths come from the LITERAL'S OWN STRUCTURE,
/// exactly as the C++ lane's computeRaggedRowLengths reads them -- the closed
/// annotation's lens binding is decorative in both lanes today. One flat pool
/// (no row table), one constant offsets global.
and private emitRaggedLit (c: Ctx) (elems: IRExpr list) (elem: Sc) (rows: int64) : ArrVal =
    if int64 (List.length elems) <> rows then
        refuse ($"a ragged literal with {(List.length elems)} rows for a declared outer extent of {rows}")
    let rowElems =
        elems |> List.map (fun e ->
            match e with
            | IRArrayLit (inner, _) -> inner
            | _ -> refuse "a ragged literal whose rows are not row literals")
    let offsets = Array.zeroCreate (List.length rowElems + 1)
    rowElems |> List.iteri (fun i r -> offsets.[i + 1] <- offsets.[i] + int64 (List.length r))
    let total = offsets.[List.length rowElems]
    let sym = raggedTableGlobal c offsets
    let ptr = allocPool c elem (string total)
    rowElems |> List.concat |> List.iteri (fun i x ->
        let v = coerce c elem (emitExpr c x)
        storeCell c elem (gepCell c elem ptr (string i)) v)
    { Elem = elem; Groups = [ GRagged (string rows, RtStatic (offsets, sym)) ]
      Src = APool ptr; RowOpBytes = 0L }

/// A literal array. The IR nests one `IRArrayLit` per rank level; the pool is
/// its row-major flattening, which is the declared shape's traversal order.
and private emitArrayLit (c: Ctx) (elems: IRExpr list) (arrType: IRArrayType) : ArrVal =
    match raggedArrShape (Blade.IR.mkArrayLike arrType) with
    | Some (elem, rows) -> emitRaggedLit c elems elem rows
    | None ->
    let (elem, groups) = requireArray "an array literal" (Blade.IR.mkArrayLike arrType)
    let rec flatten (es: IRExpr list) : IRExpr list =
        es |> List.collect (fun x ->
            match x with
            | IRArrayLit (inner, _) -> flatten inner
            | other -> [ other ])
    let flat = flatten elems
    let total = shapeCells groups
    if int64 flat.Length <> total then
        refuse ($"an array literal with {flat.Length} values for a shape of {total} cells")
    let ptr = allocPool c elem (string total)
    flat |> List.iteri (fun i x ->
        let v = coerce c elem (emitExpr c x)
        storeCell c elem (gepCell c elem ptr (string i)) v)
    { Elem = elem; Groups = groups; Src = APool ptr; RowOpBytes = 0L }

/// The unchanged dense fill, shared by fresh materialization and an A03
/// return whose existing owned backing has passed the realization checks.
and private fillDensePool (c: Ctx) (ptr: string) (a: ArrVal) : ArrVal =
    emitNest c a.Extents (fun idxs ->
        let v = coerce c a.Elem (readCell c a idxs)
        storeCell c a.Elem (gepCell c a.Elem ptr (storageOffset c a.Groups idxs)) v)
    { a with Src = APool ptr }

/// Run a producer into a fresh pool. The ONLY place an array acquires
/// storage in this lane, and therefore the one place the iteration strategy
/// for a shape is chosen. Four shapes, four strategies:
///
///   * already a pool                -> nothing to do;
///   * FLAT producer (any shape)     -> one counted loop over the pool. This
///     is the elementwise path the blocked-simplex plan tells us to leave
///     alone (section 6): zero coordinate arithmetic, whatever the shape;
///   * one rank-2 compact group      -> the BLOCKED SIMPLEX nest. A map's
///     cells are distinct and its writes independent, so brick order is free
///     and needs no licence (section 7, "Maps: brick order free");
///   * anything else                 -> the shape's plain nest.
and private materialize (c: Ctx) (a: ArrVal) : ArrVal =
    match a.Src with
    | APool _ -> a
    | AVirtFlat f ->
        let total = shapeCellsOp c a.Groups
        let ptr = allocPool c a.Elem total
        emitCountedLoopTo c total (fun k ->
            let v = coerce c a.Elem (f c { Reg = k; Ty = ScI64 })
            storeCell c a.Elem (gepCell c a.Elem ptr k) v)
        { a with Src = APool ptr }
    | AVirt _ when (match a.Groups with [ GDomain _ ] -> true | _ -> false) ->
        // A domain producer: one pool of Card cells, filled by the affine
        // nest in lex order (the counter is the offset).
        let plan = match a.Groups with [ GDomain p ] -> p | _ -> refuse "unreachable"
        let ptr = allocPool c a.Elem (string (max plan.Card 1L))
        emitDomainNest c plan (fun xs off ->
            let v = coerce c a.Elem (readCell c a xs)
            storeCell c a.Elem (gepCell c a.Elem ptr off.Reg) v)
        { a with Src = APool ptr }
    | AVirt _ when hasRagged a.Groups ->
        // A coordinate producer over the ragged pair: the nest is the ragged
        // walk itself -- outer rows, inner bound off the table, pool offset
        // one add past the row base.
        (match a.Groups with
         | [ GRagged (rows, table) ] ->
             let total = shapeCellsOp c a.Groups
             let ptr = allocPool c a.Elem total
             emitCountedLoopTo c rows (fun g ->
                 let off = raggedOffAt c table g
                 emitCountedLoopTo c (raggedLenAt c table g) (fun j ->
                     let v = coerce c a.Elem (readCell c a [ { Reg = g; Ty = ScI64 }; { Reg = j; Ty = ScI64 } ])
                     storeCell c a.Elem (gepCell c a.Elem ptr (i64Add c off j)) v))
             { a with Src = APool ptr }
         | _ -> refuse "a coordinate producer over a mixed ragged shape")
    // A rank-1 producer whose extent is an OPERAND (a peel over a
    // runtime-counted grouping, a map over a rank-1 compound): one counted
    // loop, the bound taken as-is.
    | AVirt _ when (match a.Groups with [ GDynDense _ ] | [ GCompact _ ] -> true | _ -> false) ->
        let len = match a.Groups with [ GDynDense l ] | [ GCompact (l, _) ] -> l | _ -> refuse "unreachable"
        let ptr = allocPool c a.Elem len
        emitCountedLoopTo c len (fun k ->
            let v = coerce c a.Elem (readCell c a [ { Reg = k; Ty = ScI64 } ])
            storeCell c a.Elem (gepCell c a.Elem ptr k) v)
        { a with Src = APool ptr }
    | AVirt _ when isDenseShape a.Groups ->
        let total = shapeCells a.Groups
        let ptr = allocPool c a.Elem (string total)
        fillDensePool c ptr a
    | AVirt _ ->
        match trySyrkTileCore c a with
        | Some pooled -> pooled
        | None ->
        match tryRowTileCore c a with
        | Some pooled -> pooled
        | None ->
        let total = shapeCells a.Groups
        let ptr = allocPool c a.Elem (string total)
        (match soleSimplexR a.Groups with
         | Some (r, n, strict) ->
             // A MAP's cells are independent, so the brick order is free (no
             // licence question); the tile edge is the reuse policy's call.
             // Blocking is defined at rank 2 only, so rank 3+ takes `None` and
             // runs the serial simplex rather than refusing a shape it can
             // perfectly well emit.
             let tile = if r = 2 then brickTileEdge true a.RowOpBytes n else None
             emitSimplexR c r n strict tile (fun emit -> emit ())
                 (fun coords off ->
                     let v = coerce c a.Elem (readCell c a coords)
                     storeCell c a.Elem (gepCell c a.Elem ptr off.Reg) v)
         | None ->
             emitShapeNest c a.Groups (fun idxs off ->
                 let v = coerce c a.Elem (readCell c a idxs)
                 storeCell c a.Elem (gepCell c a.Elem ptr off.Reg) v))
        { a with Src = APool ptr }

/// THE TILE CORE (bench/notes/microkernel-sketches-review.md (removed 2026-10-24; in git history at cf2028cc) item M6): run a
/// same-array `comm` row contraction (a `SyrkCandidate` applyToArr attached)
/// into its packed-triangle pool with ONE call to blade_llvm_tiles.c's
/// `blade_dsyrk_tri_f64` -- the C++ lane's 6 x 8 register tile, the same
/// blade_tiles.h text, compiled by clang under this lane's contraction
/// policy -- instead of the 64 x 64 brick nest with a per-cell dot. None
/// keeps the nest: no candidate, BLADE_SYRK_TILE=0, below the crossover
/// (`packedTriWorth`, the C++ lane's one statement of it), or a module with a
/// `where repro` function (this lane does not yet honour per-function repro,
/// and the tile object is compiled for the BUILD's contraction mode).
///
/// NO LICENCE NEEDED, as in the C++ lane: every cell is ONE chain, +0.0 then
/// `acc += X[i][s] * X[j][s]` for s ascending; K blocks store and reload the
/// exact partial. Bitwise the serial prodsum under BLADE_FP_CONTRACT=off and
/// the fma-per-step chain under the default -- the C++ lane's tile bits in
/// both modes (`blade test llvm-tiles`). The nest's per-cell dot was a
/// REASSOCIATED chain (`reassoc nsz`) in the default build, so the tile
/// moves the default's bits by ulps toward the serial chain, the same move
/// the C++ lane's tile made.
///
/// The kernel's scalar tail (`prodsum(a, b) / d`, `* s`) is applied by the
/// core to the FINISHED triangle, the post-pass the C++ lane applies.
and private trySyrkTileCore (c: Ctx) (a: ArrVal) : ArrVal option =
    match syrkCandidates.TryGetValue a with
    | true, cand ->
        let p, k = cand.P, cand.K
        let kc = Blade.CodeGenExprSupport.syrkTileKc k
        let record (rule: string) (outcome: Blade.Effects.DecisionOutcome) (evidence: string list) =
            if Blade.Effects.Decisions.active () then
                Blade.Effects.Decisions.record
                    { Rule = rule; Version = 1; Span = c.DeclSpan
                      Subject = (if c.DeclName = "" then "main" else c.DeclName)
                      Outcome = outcome; Evidence = evidence }
        let decline (why: string) =
            record "microkernel" (Blade.Effects.Declined why)
                [ "packed syrk tile (shared C core)"; $"P={p} K={k}" ]
            None
        if not (Blade.CodeGenExprSupport.syrkTileEnabled ()) then
            decline "BLADE_SYRK_TILE=0/off: the brick nest with a per-cell dot (A/B knob)"
        elif not (Blade.CodeGenExprSupport.packedTriWorth p k) then
            decline "below the syrk-tile crossover (p >= 16, k >= 4): the per-cell nest wins"
        elif c.Callables |> Map.exists (fun _ cl -> cl.IsRepro) then
            decline "a `where repro` function in the module (the tile object follows the build's contraction mode, not a function's)"
        else
        // The scale, evaluated ONCE, here -- the kernel would evaluate the
        // same literal / non-parameter variable in this same context per cell.
        let scaleMode, scaleVal =
            match cand.Scale with
            | None -> "0", f64Const 1.0
            | Some (Blade.LinAlgPatterns.ScaleDiv d) -> "1", (coerce c ScF64 (emitExpr c d)).Reg
            | Some (Blade.LinAlgPatterns.ScaleMul s) -> "2", (coerce c ScF64 (emitExpr c s)).Reg
        let x = materialize c cand.Operand
        let xPtr =
            match x.Src with
            | APool ptr -> ptr
            | _ -> refuse "a tile-core operand that did not materialize to a pool"
        let total = shapeCells a.Groups
        let out = allocPool c ScF64 (string total)
        let g = Blade.CodeGenExprSupport.syrkRowGroup ()
        let edge = Blade.CodeGenExprSupport.triTileEdge ()
        let scratchCells = syrkTileScratchCells p kc g
        let scratch = allocPool c ScF64 (string scratchCells)
        let scaleNote =
            match cand.Scale with
            | Some (Blade.LinAlgPatterns.ScaleDiv _) -> ", then / d per cell (fused into the last block's store)"
            | Some (Blade.LinAlgPatterns.ScaleMul _) -> ", then * s per cell (fused into the last block's store)"
            | None -> ""
        // The declaration's attributes are claims about blade_llvm_tiles.c
        // (stated there): argument memory only, serial, returns. Parameter
        // facts ride the fact layer's switch like every other declaration.
        let pa = paramAttr ()
        let ro, nc =
            if factEnabled factParamAttrs then " readonly captures(none)", " captures(none)" else "", ""
        need c ($"declare void @{tilesSymbol}(i64{pa}, i64{pa}, ptr{pa}{ro}, i64{pa}, ptr{pa}{nc}, i64{pa}, i64{pa}, i64{pa}, i64{pa}, i64{pa}, double{pa}, ptr{pa}{nc}, i64{pa}){(attrRef c grpTileCore)}")
        ln c ($"; syrk tile (shared C core blade_llvm_tiles.c over blade_tiles.h, 6x8, KC={kc}, row groups of {g}): per cell the serial prodsum")
        ln c (renderCall { Dest = None; RetTy = ScVoid; Callee = "@" + tilesSymbol
                           Args = [ ScI64, string p; ScI64, string k; ScStr, xPtr; ScI64, string k
                                    ScStr, out; ScI64, "0"
                                    ScI64, string Blade.CodeGenExprSupport.syrkTileKcMax
                                    ScI64, string g
                                    ScI64, string edge
                                    ScI64, scaleMode; ScF64, scaleVal
                                    ScStr, scratch; ScI64, string scratchCells ] })
        record "microkernel" Blade.Effects.Applied
            [ "packed syrk tile (shared C core blade_llvm_tiles.c over blade_tiles.h, 6x8 register tile over the packed triangle)"
              $"P={p} K={k} KC={kc} G={g}" ]
        record "fold-form" Blade.Effects.Applied
            [ $"fold form: syrk tile (shared C core) 6x8, KC={kc}, row groups of {g}{Blade.CodeGenExprSupport.triEdgeNote edge}{scaleNote}"
              "same-array comm row contraction (one ascending chain per cell; no reorder licence used; LLVM lane, serial)" ]
        Some { a with Src = APool out; RowOpBytes = 0L }
    | _ -> None

/// THE ROW-CONTRACTION TILE CORES (review items M7 / M8): the antisym pair
/// and the rank-3 simplex (a `RowTileCandidate` applyToArr attached) run into
/// their packed pools with ONE call each to blade_llvm_tiles.c --
/// `blade_dantisym_tri_f64` (the C++ lane's blade_pgemm::dantisym_tri:
/// sequential 6 x 8 or, under BLADE_ANTISYM_TILE=4x4, paired 4 x 4) or
/// `blade_dsym3_tri_f64` (blade_pgemm::dsym3_tri: the W = X(i) * X(j) panel
/// and the 6 x 8 tile over k) -- the same blade_tiles.h text, compiled by
/// clang under this lane's contraction policy. None keeps the nest: no
/// candidate, the knob off, below the crossover (`packedAntiWorth` /
/// `packedSym3Worth`, the C++ lane's statements of it), a component
/// subscript outside the literal component extent, or a module with a
/// `where repro` function (the object follows the BUILD's contraction mode).
///
/// NO LICENCE NEEDED, so the tiles are ON under BLADE_FP_REPRO=1: every cell
/// keeps its operation sequence (the pair: two ascending chains in source
/// factor order, then ONE subtraction; the simplex: one ascending `(a*b)*c`
/// chain, K blocks storing and reloading the exact unscaled partial), then
/// the kernel's scalar once on the finished value. Bitwise the serial form
/// under BLADE_FP_CONTRACT=off and the one-fma-per-step chains under the
/// default -- the C++ lane's tile bits in both modes (`blade test llvm tiles`).
and private tryRowTileCore (c: Ctx) (a: ArrVal) : ArrVal option =
    match rowTileCandidates.TryGetValue a with
    | true, cand ->
        let p, n = cand.P, cand.N
        let record (rule: string) (outcome: Blade.Effects.DecisionOutcome) (evidence: string list) =
            if Blade.Effects.Decisions.active () then
                Blade.Effects.Decisions.record
                    { Rule = rule; Version = 1; Span = c.DeclSpan
                      Subject = (if c.DeclName = "" then "main" else c.DeclName)
                      Outcome = outcome; Evidence = evidence }
        let tileName, shapeEv =
            match cand.Shape with
            | Blade.LinAlgPatterns.AntiPair (cx, cy) ->
                "antisym tile (shared C core)", $"P={p} N={n} components=({cx},{cy})"
            | Blade.LinAlgPatterns.Sym3 -> "simplex tile (shared C core)", $"P={p} N={n}"
        let decline (why: string) =
            record "microkernel" (Blade.Effects.Declined why) [ tileName; shapeEv ]
            None
        let reproFn = c.Callables |> Map.exists (fun _ cl -> cl.IsRepro)
        let reproWhy =
            "a `where repro` function in the module (the tile object follows the build's contraction mode, not a function's)"
        // The scale, evaluated ONCE, here -- the kernel would evaluate the
        // same literal / non-parameter variable in this same context per cell.
        let scaleArgs () =
            match cand.Scale with
            | None -> "0", f64Const 1.0
            | Some (Blade.LinAlgPatterns.ScaleDiv d) -> "1", (coerce c ScF64 (emitExpr c d)).Reg
            | Some (Blade.LinAlgPatterns.ScaleMul s) -> "2", (coerce c ScF64 (emitExpr c s)).Reg
        let scaleNote =
            match cand.Scale with
            | Some (Blade.LinAlgPatterns.ScaleDiv _) -> ", then / d per cell (fused into the last block's store)"
            | Some (Blade.LinAlgPatterns.ScaleMul _) -> ", then * s per cell (fused into the last block's store)"
            | None -> ""
        let operandPtr () =
            let x = materialize c cand.Operand
            match x.Src with
            | APool ptr -> ptr
            | _ -> refuse "a tile-core operand that did not materialize to a pool"
        // The declaration's attributes are claims about blade_llvm_tiles.c
        // (stated there), the syrk core's: argument memory only, serial,
        // returns. Parameter facts ride the fact layer's switch.
        let pa = paramAttr ()
        let ro, ncap =
            if factEnabled factParamAttrs then " readonly captures(none)", " captures(none)" else "", ""
        match cand.Shape with
        | Blade.LinAlgPatterns.AntiPair (cx, cy) ->
            match Blade.CodeGenExprSupport.antisymTileSchedule () with
            | Blade.CodeGenExprSupport.AntiOff ->
                decline "BLADE_ANTISYM_TILE=0/off: the plain per-cell nest (A/B knob)"
            | sched ->
            if not (cx < cand.Comps && cy < cand.Comps) then
                decline "component subscripts not proven inside the literal component extent"
            elif not (Blade.CodeGenExprSupport.packedAntiWorth p n) then
                decline "below the antisym-tile crossover (p >= 24, n >= 8): the per-cell nest wins"
            elif reproFn then decline reproWhy
            else
            let paired = (sched = Blade.CodeGenExprSupport.AntiPaired4x4)
            let form =
                if paired then "paired 4x4 (P and Q in one pass)"
                else "sequential 6x8 (P pass, spill, Q pass)"
            let scaleMode, scaleVal = scaleArgs ()
            let zPtr = operandPtr ()
            let out = allocPool c ScF64 (string (shapeCells a.Groups))
            // Row-panel groups: the C++ lane's rule (antisymRowGroup) in its
            // SERIAL regime (this lane has no thread runtime, so the default
            // is G = 1), the scratch sized for them (a short scratch would
            // silently drop the core to its scalar chain).
            let g, groupNote = Blade.CodeGenExprSupport.antisymRowGroup p n paired false
            let scratchCells = antisymTileScratchCells p n paired g
            let scratch = allocPool c ScF64 (string scratchCells)
            let kcMax = Blade.CodeGenExprSupport.antisymKcMax ()
            let chainNote =
                if kcMax = 0L || kcMax >= n then "whole chain per tile"
                else $"whole chain per tile in sample blocks of <= {kcMax}"
            need c ($"declare void @{antisymTileSymbol}(i64{pa}, i64{pa}, ptr{pa}{ro}, i64{pa}, i64{pa}, i64{pa}, i64{pa}, ptr{pa}{ncap}, i64{pa}, i64{pa}, i64{pa}, i64{pa}, i64{pa}, double{pa}, ptr{pa}{ncap}, i64{pa}){(attrRef c grpTileCore)}")
            ln c ($"; antisym tile (shared C core blade_llvm_tiles.c over blade_tiles.h, {form}, row groups of {g}): per cell the serial prodsum - prodsum")
            ln c (renderCall { Dest = None; RetTy = ScVoid; Callee = "@" + antisymTileSymbol
                               Args = [ ScI64, string p; ScI64, string n; ScStr, zPtr
                                        ScI64, string (cand.Comps * n); ScI64, string n
                                        ScI64, string cx; ScI64, string cy
                                        ScStr, out; ScI64, (if paired then "1" else "0"); ScI64, "0"
                                        ScI64, string kcMax
                                        ScI64, string g
                                        ScI64, scaleMode; ScF64, scaleVal
                                        ScStr, scratch; ScI64, string scratchCells ] })
            record "microkernel" Blade.Effects.Applied
                [ $"antisym tile (shared C core blade_llvm_tiles.c over blade_tiles.h, {form}, strict packed triangle)"
                  $"{shapeEv} KC={kcMax} G={g}" ]
            record "fold-form" Blade.Effects.Applied
                [ $"fold form: antisym tile (shared C core) {form}, {chainNote}, {groupNote}{scaleNote}"
                  "same-array anticomm row-contraction pair (two ascending chains per cell, one subtraction; no reorder licence used; LLVM lane, serial)" ]
            Some { a with Src = APool out; RowOpBytes = 0L }
        | Blade.LinAlgPatterns.Sym3 ->
            if not (Blade.CodeGenExprSupport.simplexTileEnabled ()) then
                decline "BLADE_SIMPLEX_TILE=0/off: the plain per-cell nest (A/B knob)"
            elif not (Blade.CodeGenExprSupport.packedSym3Worth p n) then
                decline "below the simplex-tile crossover (p >= 24, n >= 4): the per-cell nest wins"
            elif reproFn then decline reproWhy
            else
            let kc = Blade.CodeGenExprSupport.syrkTileKc n
            let scaleMode, scaleVal = scaleArgs ()
            let xPtr = operandPtr ()
            let out = allocPool c ScF64 (string (shapeCells a.Groups))
            let scratchCells = sym3TileScratchCells p n kc
            let scratch = allocPool c ScF64 (string scratchCells)
            let edge = Blade.CodeGenExprSupport.triTileEdge ()
            need c ($"declare void @{simplexTileSymbol}(i64{pa}, i64{pa}, ptr{pa}{ro}, i64{pa}, ptr{pa}{ncap}, i64{pa}, i64{pa}, i64{pa}, double{pa}, ptr{pa}{ncap}, i64{pa}){(attrRef c grpTileCore)}")
            ln c ($"; simplex tile (shared C core blade_llvm_tiles.c over blade_tiles.h, W panel + 6x8, KC={kc}): per cell the serial prodsum")
            ln c (renderCall { Dest = None; RetTy = ScVoid; Callee = "@" + simplexTileSymbol
                               Args = [ ScI64, string p; ScI64, string n; ScStr, xPtr; ScI64, string n
                                        ScStr, out
                                        ScI64, string Blade.CodeGenExprSupport.syrkTileKcMax
                                        ScI64, string edge
                                        ScI64, scaleMode; ScF64, scaleVal
                                        ScStr, scratch; ScI64, string scratchCells ] })
            record "microkernel" Blade.Effects.Applied
                [ "simplex tile (shared C core blade_llvm_tiles.c over blade_tiles.h, W = X(i) * X(j) panel x 6x8 register tile over k, packed SymIdx<3> pool)"
                  $"P={p} N={n} KC={kc}" ]
            record "fold-form" Blade.Effects.Applied
                [ $"fold form: simplex tile (shared C core) 6x8 with W panel, KC={kc}{Blade.CodeGenExprSupport.triEdgeNote edge}{scaleNote}"
                  "same-array 3-way comm row contraction (one ascending (a*b)*c chain per cell; no reorder licence used; LLVM lane, serial)" ]
            Some { a with Src = APool out; RowOpBytes = 0L }
    | _ -> None

/// Materialize an array EXPRESSION in place: if it names a binding, the
/// binding's slot is upgraded to the pool, so later reads share the storage
/// instead of re-running the kernel -- and so a `mut` callee writing through
/// the pointer is visible to the caller.
/// The ARRAY-OPERAND RULE every eager consumer shares (reduce, prodsum,
/// IRIndex, group_keys, group_by, decompact): a multi-read name FORCES, so its
/// readers share one pool; a sole-read module binding is CONSUMED where it
/// stands but marked forced, because forcing is what makes a deferred
/// binding print (the pool would not dominate a print after a loop nest);
/// anything else is consumed where it stands. emitReduce spells the same
/// rule out longhand, with the history.
and private consumeArrOperand (c: Ctx) (e: IRExpr) : ArrVal =
    match e with
    | IRVar (id, _) when not (c.Facts.ReadOnce.Contains id) -> materializeExpr c e
    // A PARTIAL reader retains the in-place consumption only when nothing
    // can tell: no assignment anywhere (the print's rerun reads the same
    // cells) and a producer that cannot fail (no unread cell's failure to
    // move) -- otherwise the whole map is stored at the statement.
    | IRVar (id, _) when c.BindingValues.ContainsKey id ->
        soleReadModuleOperand c id e (not c.Facts.AnyAssignment && producerCannotFail c id)
    | _ -> emitArr c e

/// The C++ lane's tupleNeedsMemberOrder (CodeGenBinding.fs), over this lane's
/// facts: some leaf is STRUCTURAL -- an explicit compute, a bare deferred
/// array (a producer slot), a named deferred tuple (every part a producer) --
/// or at least TWO leaves may fail (memberMayFail's whitelist: names,
/// literals, stored reads, projections / extents of them, and `+ - * /`
/// arithmetic, comparisons and logic, an integer `/` `%`, a possibly negative
/// integer `^` and a float -> int cast counting as failing; any other form
/// may fail).
and private tupleNeedsMemberOrderLl (c: Ctx) (elems: IRExpr list) : bool =
    let isProducer (id: IRId) =
        match c.ArrSlots.TryGetValue id with
        | true, { Src = APool _ } -> false
        | true, _ -> true
        | _ -> false
    let isDeferredTuple (id: IRId) =
        match c.TupleSlots.TryGetValue id with
        | true, parts ->
            not parts.IsEmpty && parts |> List.forall (function VArray { Src = (AVirt _ | AVirtFlat _) } -> true | _ -> false)
        | _ -> false
    let elemOf (x: IRExpr) = try Blade.IRTotality.scalarElem (typeOf x) with _ -> None
    let isInt (x: IRExpr) = match elemOf x with Some et -> Blade.IRTotality.isInt et | None -> false
    let isFloat (x: IRExpr) = match elemOf x with Some et -> Blade.IRTotality.isFloat et | None -> false
    let rec mayFail (x: IRExpr) =
        match x with
        | IRLit _ | IRParam _ -> false
        | IRVar (id, _) -> isProducer id || isDeferredTuple id
        | IRBinOp (_, op, a, b, _) ->
            (match op with
             | IRDiv | IRMod -> isInt a || isInt b
             | IRCaret -> isInt b && (match b with IRLit (IRLitInt n) -> n < 0L | _ -> true)
             | _ -> false)
            || mayFail a || mayFail b
        | IRUnaryOp (IRCast (et, _), a) -> (Blade.IRTotality.isInt et && isFloat a) || mayFail a
        | IRUnaryOp (_, a) -> mayFail a
        | IRTupleProj (a, _, _) | IRFieldAccess (a, _) | IRExtent (a, _) -> mayFail a
        | IRTuple es -> es |> List.exists mayFail
        | IRIf (cnd, t, f) -> mayFail cnd || mayFail t || mayFail f
        | _ -> true
    let rec strip (x: IRExpr) = match x with IRCompute i -> strip i | x -> x
    let rec leaves (x: IRExpr) = match x with IRTuple es -> es |> List.collect leaves | x -> [ x ]
    let ls = elems |> List.collect leaves
    // the SHARED structural judgment (IRLift.memberNeedsOrderOf, the one the
    // C++ emitter and the lowering refusal use) over a live view of this
    // lane's deferred state; a named deferred tuple this lane holds as
    // producer parts counts too (its value is not in the module table when
    // it is block-local)
    let dv : Blade.IRLift.DeferredView =
        { IsDeferred = (fun id -> isProducer id || isDeferredTuple id)
          ValueOf = (fun id -> Map.tryFind id c.BindingValues)
          TypeOfExpr = typeOf }
    let structural (x: IRExpr) =
        Blade.IRLift.memberNeedsOrderOf dv x
        || (match x with IRVar (id, _) -> isDeferredTuple id | _ -> false)
    let leafMayFail (x: IRExpr) =
        match strip x with
        | IRVar _ | IRParam _ | IRLit _ -> false
        | s -> mayFail s
    (ls |> List.exists structural) || (ls |> List.filter leafMayFail |> List.length) >= 2

/// consumeArrOperand for a consumer that reads EVERY cell of the operand and
/// cannot fail itself (prodsum): the retention certificate is then only "no
/// assignment anywhere" (soleReadModuleOperand).
and private consumeArrOperandAllTotal (c: Ctx) (e: IRExpr) : ArrVal =
    match e with
    | IRVar (id, _) when not (c.Facts.ReadOnce.Contains id) -> materializeExpr c e
    | IRVar (id, _) when c.BindingValues.ContainsKey id -> soleReadModuleOperand c id e (not c.Facts.AnyAssignment)
    | _ -> emitArr c e

/// consumeArrOperand for a consumer that reads every cell, cannot fail
/// itself, but may read the cells in ANOTHER order than the force's ascending
/// one (transpose, decompact, group_by's gather, gram, negate): retention
/// then also needs the PRODUCER to be unable to fail (its map kernel passes
/// IRTotality with the lane's deferred facts), so no first failure can move.
and private consumeArrOperandAnyOrderTotal (c: Ctx) (e: IRExpr) : ArrVal =
    match e with
    | IRVar (id, _) when not (c.Facts.ReadOnce.Contains id) -> materializeExpr c e
    | IRVar (id, _) when c.BindingValues.ContainsKey id ->
        soleReadModuleOperand c id e (not c.Facts.AnyAssignment && producerCannotFail c id)
    | _ -> emitArr c e

/// The deferred module map `id` cannot fail -- its WHOLE evaluation: its value
/// is a map application whose kernel passes IRTotality's closed whitelist
/// with the lane's deferred facts (any OTHER array still held as a producer
/// is an unaudited read) AND every input array it iterates is stored (a pool)
/// or a plain range -- an input that is itself a deferred map could fail
/// (review codex-diff-W4B-B-5 finding 1), and any other input form is not
/// audited, so it declines.
and private producerCannotFail (c: Ctx) (id: IRId) : bool =
    let isDeferred (vid: IRId) =
        vid <> id
        && (match c.ArrSlots.TryGetValue vid with
            | true, { Src = APool _ } -> false
            | true, _ -> true
            | _ -> false)
    let inputStored (a: IRExpr) =
        match a with
        | IRVar (vid, _) -> (match c.ArrSlots.TryGetValue vid with | true, { Src = APool _ } -> true | _ -> false)
        | IRRange _ -> true
        | _ -> false
    match Map.tryFind id c.BindingValues with
    | Some (IRApplyCombinator info) ->
        info.Arrays |> List.forall inputStored
        && (Blade.IRTotality.auditApply resolveCallable (fun _ -> None) isDeferred info |> Result.isOk)
    | _ -> false

/// A SOLE-READ deferred MODULE binding at a forcing use (gap G60 (b)). Its
/// class value is fixed at its FIRST FORCING EVENT (formalism 10.2): the
/// C++ lane forces it under its own name at the statement and prints those
/// cells, so a later write to an input cannot change what prints. Where the
/// force can be HOISTED to the start of main's current statement
/// (`forceHoistable`: main, no tracking scope open, the slot tables
/// unchanged since the statement began) it is materialized there -- the pool
/// dominates every later statement and the print, and the print reads the
/// stored cells instead of rerunning the producer (a force nested inside
/// another hoisted force hoists to the start of that hoisted block). Elsewhere
/// (an open `let rec` trip scope at main level, a statement whose slots were
/// already upgraded in place) a pool would not dominate the print, so the
/// operand is consumed where it stands and only marked printable, as before:
/// there the print still REDOES the producer (the recorded residual of
/// G60 (b)). A function body's PRIVATE map (G75) that cannot be stored at its
/// statement (a read inside a loop scope of the body) is refused instead.
///
/// `retainFused` is the caller's CERTIFICATE that consuming in place is
/// indistinguishable from the force (a fold: no assignment anywhere in the
/// program, so nothing the map reads changes before the print's rerun, and a
/// fold kernel and seed that cannot fail, so the first failing cell is the
/// producer's own, in the same ascending order): then the fused consumption
/// (no pool) is kept -- the fastest form. A PARTIAL reader (`g(0)`) never
/// has one: the force evaluates every cell at the statement (C++ and the
/// interpreter), so a failing cell it does not read must fail there.
and private soleReadModuleOperand (c: Ctx) (id: IRId) (e: IRExpr) (retainFused: bool) : ArrVal =
    match c.ArrSlots.TryGetValue id with
    | true, { Src = APool _ } -> emitArr c e
    | _ when retainFused ->
        markForced c id
        emitArr c e
    | _ when forceHoistable c id -> materializeExpr c e
    | _ ->
        refusePrivateUnhoistable c id
        markForced c id
        emitArr c e

/// A function body forces a PRIVATE deferred module map (G75) only where it
/// can store it with the frame's lifetime at the body's current statement
/// (forceHoistable); a force the body would run inside one of its loop
/// scopes (a `let rec` trip, a rank-k fold) would allocate once per trip and
/// hand later reads a register that does not dominate them -- refused, fail
/// closed (the program runs in the C++ lane).
and private refusePrivateUnhoistable (c: Ctx) (id: IRId) : unit =
    if c.InFunction && c.DeferredModule.ContainsKey id then
        match c.ArrSlots.TryGetValue id with
        | true, { Src = APool _ } -> ()
        | _ ->
            let nm = defaultArg (Map.tryFind id c.ModuleNames) (string id)
            refuse ($"a function body forcing the deferred module binding '{nm}' inside one of its loop scopes (its private evaluation is stored at the body's statement only)")

/// Can forcing module binding `id` be hoisted to the start of the current
/// statement -- main's top-level statement, or a spine statement of a
/// function body (its frame's scope the only one open)? (materializeExpr's
/// hoist, below, says why each condition is needed.)
and private forceHoistable (c: Ctx) (id: IRId) : bool =
    c.StmtStart >= 0 && c.PoolScopes.Count = (if c.InFunction then 1 else 0)
    && c.BindingValues.ContainsKey id
    && c.StmtSlots.ContainsKey id
    && c.StmtSlots |> Seq.forall (fun kv ->
           match c.ArrSlots.TryGetValue kv.Key with
           | true, now -> obj.ReferenceEquals(now, kv.Value) || c.HoistedIds.Contains kv.Key
           | _ -> true)

and private materializeExpr (c: Ctx) (e: IRExpr) : ArrVal =
    // The producer object this name held BEFORE the force: a deferred alias
    // (`let g2 = g`) is bound to the SAME ArrVal as its source (classifyValue
    // of the alias returns the source's slot), so reference equality is the
    // alias class (gap G60). Every slot still holding it is upgraded to the
    // forced pool below -- one stored value per class from the forcing event
    // on, so a later read or print of any alias reads those cells and never
    // reruns the producer over inputs that changed since (Codex second look
    // of W4-1, finding 3).
    let before =
        match e with
        | IRVar (id, _) ->
            (match c.ArrSlots.TryGetValue id with
             | true, old -> (match old.Src with APool _ -> None | _ -> Some old)
             | _ -> None)
        | _ -> None
    let a =
        match e with
        // FORCING A DEFERRED MODULE BINDING HOISTS to the start of main's
        // current top-level statement -- the C++ lane's
        // forceDeferredPositionalReads / forceDeferredArrayInput, which emit
        // the force at the enclosing statement's indent. Inline, a consumer
        // sitting in a loop nest (a kernel reading `c((1 : X))` once per
        // cell) would allocate the pool inside the nest, and the slot upgrade
        // below would hand later statements a register that does not
        // dominate them (corpus loops/129). The producer closes only over
        // registers of EARLIER statements, so the hoisted code is well
        // formed; with no tracking scope open nothing else indexes `c.Body`.
        // A force NESTED inside the hoist (the producer's kernel reads
        // another deferred module binding) hoists in turn, to the start of
        // the hoisted block -- ahead of this producer's code, which is the
        // C++ lane's order (forceDeferredArrayInput forces the inputs first)
        // -- so its pool, too, dominates the rest of main and its print reads
        // the stored cells (gap G60 (b): it used to stay inline, consumed per
        // cell, and the print reran it over inputs written since).
        //
        // The producer reads module arrays BY NAME when it is emitted, so the
        // hoist is sound only while every array binding that existed at
        // statement start still holds either that same instance or one an
        // earlier hoist of THIS statement produced (which sits in the prefix,
        // ahead of this block). Anything upgraded inline mid-statement would
        // be read here before its definition; then the force stays inline.
        // Bindings born in the statement do not count: a module binding's
        // producer cannot name them.
        | IRVar (id, _) when forceHoistable c id ->
            let outer = c.Body
            let at = c.StmtStart
            c.Body <- ResizeArray()
            c.StmtStart <- 0
            try materialize c (emitArr c e)
            finally
                let hoisted = c.Body
                c.Body <- outer
                c.Body.InsertRange(at, hoisted)
                c.StmtStart <- at + hoisted.Count
                c.HoistedIds.Add id |> ignore
        | IRVar (id, _) ->
            refusePrivateUnhoistable c id
            materialize c (emitArr c e)
        | _ -> materialize c (emitArr c e)
    (match e with
     | IRVar (id, _) when c.ArrSlots.ContainsKey id ->
         c.ArrSlots.[id] <- a
         markForced c id
         (match before with
          | Some old ->
              let sharers =
                  [ for KeyValue (k, v) in c.ArrSlots do
                      if k <> id && obj.ReferenceEquals(v, old) then yield k ]
              for k in sharers do
                  c.ArrSlots.[k] <- a
                  if c.BindingValues.ContainsKey k then markForced c k
              let named =
                  [ for KeyValue (n, v) in c.NameArrSlots do
                      if obj.ReferenceEquals(v, old) then yield n ]
              for n in named do c.NameArrSlots.[n] <- a
          | None -> ())
         // The upgraded binding may OUTLIVE an open tracking scope: it
         // escapes any scope it predates (its later reads happen after that
         // scope's exit frees). A binding born inside the scope dies with it,
         // so its pool stays freeable.
         (match a.Src with
          | APool p when c.PoolScopes |> Seq.exists (fun s -> s.KnownIds.Contains id) ->
              keepPool c id p
          | _ -> ())
     | _ -> ())
    a

/// Record that binding `id` ends the program as a real array -- AND that every
/// deferred array binding its value names does too.
///
/// The recursion is the whole point, and it is the C++ lane's: materializing
/// `k2 = method_for(llsq, llsq) <@> (+)` runs `forceDeferredArrayInput` over
/// `llsq`, which materializes `llsq` under its own name, which forces `ll` in
/// turn -- so all three join the auto-print list even though the program only
/// ever asks for a scalar out of `k2` (corpus loops, "Deferred Binding Forced
/// On Read"). Marking is enough here: a producer prints by re-running, so the
/// llvm lane needs the PRINTABILITY fact, not a second pool.
///
/// A LOOP BODY IS PRUNED, and that boundary is the C++ lane's `ctx.Indent = 0`
/// guard in `forceDeferredArrayInput`: a producer materialized inside a loop
/// body becomes a per-iteration LOCAL there, block-scoped and deliberately
/// left out of the printable set, so a name this value merely MENTIONS under a
/// loop is no evidence that the binding ends the program materialized. A
/// `let rec`'s whole recursion is one such body, which is how a producer forced
/// per step (`let f = dn |> compute` inside a step block, corpus memfree/007)
/// used to reach this list through the recursion above -- `traj` names it, so
/// forcing `traj` printed `dn`, a line the C++ lane does not emit.
///
/// Pruning loses nothing, because the reads that DO print from a loop body
/// never travel this edge: the C++ lane hoists them out of the loop with
/// `forceDeferredPositionalReads` (at the enclosing indent, hence printable)
/// and `forceLoopBodyProducers` mirrors that hoist by calling this function
/// directly on each one.
and private markForced (c: Ctx) (id: IRId) : unit =
    if c.Forced.Add id then
        match Map.tryFind id c.BindingValues with
        | None -> ()
        | Some v ->
            // `iterIRExpr` with one subtree withheld. Recursion is the shared
            // `ExprShape` fold, exactly as there, so no variant can be skipped
            // by accident -- only the loop body named here.
            let rec walk (e: IRExpr) =
                match e with
                | IRForRange (_, lo, hi, _) -> walk lo; walk hi
                // `g |> compute` is a COPY of g's cells (formalism 10.2): it
                // does not force g under its own name, so a binding stored
                // from it carries no print obligation to g -- the C++ lane
                // and the interpreter print `v = g |> compute` without g. The
                // llvm lane used to mark g here once v itself was forced (a
                // positional read of v), printing g there only.
                | IRCompute (IRVar _) -> ()
                | ExprShape (children, _) ->
                    children |> List.iter walk
                    match e with
                    | IRVar (src, _) when src <> id && c.ArrSlots.ContainsKey src -> markForced c src
                    | _ -> ()
            walk v

// ---------------------------------------------------------------------------
// Kernels and loop nests
// ---------------------------------------------------------------------------

/// Bind one kernel parameter, coercing to its DECLARED type. The declared
/// type is what decides the arithmetic below it: a `range<I>` level hands the
/// kernel an i64 induction value, and a kernel declared over `Float64`
/// converts it exactly where the C++ lane's implicit conversion would.
and private bindKernelParam (c: Ctx) (p: IRParam) (arg: KArg) : unit =
    match arg with
    | KArray a ->
        (match arrayShapeOf p.Type with
         | Some (e, gs) when e = a.Elem && gs = a.Groups -> ()
         | Some _ ->
             refuse ($"kernel parameter '{p.Name}' whose declared shape disagrees with the peeled row")
         | None ->
             // A rank-polymorphic kernel param can be declared T^k and still
             // arrive as a row; accept whatever the peel produced.
             ())
        bindArray c p.VarId p.Name a
    | KScalar v ->
        let sc = match scalarTyOf p.Type with Some s -> s | None -> v.Ty
        bindScalar c p.VarId p.Name (coerce c sc v)

/// Inline a kernel at a call site: bind its parameters, emit its body. Kernels
/// are inlined rather than outlined because they routinely CAPTURE enclosing
/// bindings, and a capture list is a closure ABI this lane deliberately does
/// not have -- a capture reference resolves through the ordinary slot tables
/// instead, since IR ids are program-unique.
and private applyKernel (c: Ctx) (cl: IRCallable) (args: KArg list) : Val =
    applyKernelFramed c true cl args

/// `applyKernel`, with or without the callable's shadow frame. FRAMELESS only
/// where the C++ lane inlines the kernel's body instead of calling it -- the
/// scalar ragged PEEL (`method_for(g) <@> lambda(r) -> reduce(r, ..)`,
/// CodeGenCuda's inlined peel body) -- so a guard panic in it prints the SAME
/// frame list in every lane (gap G76: the interpreter's peel pushes none
/// either).
and private applyKernelFramed (c: Ctx) (framed: bool) (cl: IRCallable) (args: KArg list) : Val =
    if cl.IsCudaKernel || cl.IsMpiParallel then
        refuse ($"the kernel '{cl.Name}', which carries a cuda/mpi strategy")
    if List.length cl.Params <> List.length args then
        refuse ($"a kernel '{cl.Name}' applied to {(List.length args)} of its {(List.length cl.Params)} parameters")
    List.iter2 (bindKernelParam c) cl.Params args
    // The inlined body is the callable the C++ lane would have called, so it
    // gets that callable's frame (see Shadow frames).
    if framed then framePush c cl.SourceName
    // A NAMED FUNCTION in kernel position (a fold's `reduce(w, gsel, s)`,
    // a repro twin of one): the C++ lane CALLS it, so its guards are located
    // at its own declaration -- here too (gap G76, W4B-D: this lane used to
    // locate them at the fold's declaration). A lambda (`__lambda_<id>`) keeps
    // its enclosing declaration, as before.
    let named =
        not (cl.Name.StartsWith "__lambda")
        && ((Blade.IR.declSpanOf cl.Name).StartLine > 0 || (Blade.IR.declSpanOf cl.SourceName).StartLine > 0)
    let v = if named then withCalleeDecl c cl (fun () -> emitExpr c cl.Body) else emitExpr c cl.Body
    if framed then framePop c
    v

/// A `method_for(...) <@> kernel` map, as a PRODUCER.
///
/// The loop structure comes from `buildLoopNestCodeGen` -- the same IR-level
/// builder the host, CUDA and fold emitters consume -- so outer-product versus
/// co-iteration, per-array level ownership, virtual-source peeling and kernel
/// parameter assignment are decided in one place for every back end. What this
/// lane refuses is everything the builder describes but the flat pool cannot
/// express: triangular bounds (`BoundDependencies` / `StrictOffset`), fused
/// joint levels, runtime extents.
and private applyToArr (c: Ctx) (info: ApplyInfo) : ArrVal =
    let rk =
        match resolveKernel info.Kernel with
        | Some rk -> rk
        | None -> refuse "an apply-combinator whose kernel does not resolve to a callable"
    // A CLOSED-FORM DOMAIN range (`range<R>` over a static struct with linear
    // conjuncts) is a producer over the GDomain group: the kernel at the
    // cell's field values, positional. The shared loop builder describes it
    // as one runtime-extent level over a key index this lane has no use for,
    // so it never sees it; every consumer iterates the affine nest itself.
    match info.Arrays with
    | [ IRRange ([ ix ], _) ] when (match ix.Extent with IRSparseKeys (SkDomain _) -> true | _ -> false) ->
        let plan = match ix.Extent with IRSparseKeys (SkDomain p) -> p | _ -> refuse "unreachable"
        let cl = rk.Callable
        if cl.Params.Length <> plan.Levels.Length then
            refuse ($"a kernel of {cl.Params.Length} parameter(s) over the {plan.Levels.Length}-field domain {plan.Name}")
        let elem =
            match scalarTyOf cl.RetType with
            | Some s when s <> ScVoid -> s
            | _ ->
                match Blade.IR.stripUnits info.OutputType with
                | ArrayElem arr -> requireScalar "a domain map's element" arr.ElemType
                | t -> requireScalar "a domain map's result" t
        // The cell code of a closed-form domain: in Int64 -> Float64 split scope.
        let produce = fun c2 xs -> Blade.CodeGenExprSupport.withI64SplitScope true (fun () -> coerce c2 elem (applyKernel c2 cl (xs |> List.map KScalar)))
        c.DomainMapBodies.[box produce] <- cl.Body
        { Elem = elem; Groups = [ GDomain plan ]; RowOpBytes = 0L; Src = AVirt produce }
    // Every OTHER tabulated range -- a table-enumerated static struct (its
    // conjuncts are not linear, so there is no affine nest) or a SparseIdx key
    // list -- iterates a runtime key index this lane does not build.
    | arrays when arrays |> List.exists (function
                    | IRRange (ixs, _) -> ixs |> List.exists (fun ix -> ix.IxKind = IxKSparse)
                    | _ -> false) ->
        refuse "a range over a key table (a table-enumerated static struct -- its conjuncts are not linear, so it has no closed-form nest -- or a SparseIdx key list)"
    | _ ->
    // A RAGGED operand or output takes its own peel BEFORE the shared loop
    // builder runs: buildLoopNestCodeGen has no ragged story (the C++ lane
    // routes these through tryRaggedPeel for the same reason), and an
    // exception from a shared layer here would escape tryEmitProgram.
    let typeHasRaggedRow (t: IRType) =
        match Blade.IR.stripUnits t with
        | ArrayElem arr ->
            arr.IndexTypes |> List.exists (fun ix ->
                isRaggedRowKind ix.IxKind || ix.IxKind = IxKGroupOuter)
        | _ -> false
    if info.Arrays |> List.exists (fun a -> typeHasRaggedRow (typeOf a))
       || typeHasRaggedRow info.OutputType then
        applyRaggedPeel c info rk.Callable
    else
    // A WREATH (OrbIdx depth >= 2) pool is refused BEFORE the shared loop
    // builder runs, not after: `buildLoopNestCodeGen` reaches `buildSymmVec`,
    // which `failwith`s on a wreath class rather than returning. A refusal
    // this lane raises itself falls back to the C++ lane; an exception from a
    // shared layer escapes `tryEmitProgram` and takes the harness with it.
    (match Blade.IR.stripUnits info.OutputType with
     | ArrayElem arr when arr.IndexTypes |> List.exists (fun ix -> ix.Symmetry = SymWreath) ->
         refuse "an OrbIdx (iterated-wreath) output pool"
     | _ -> ())
    // A virtual multi-slot source consumes one scalar parameter PER axis;
    // real operands consume one parameter each (IRStorage's paramSpan rule).
    let paramSpans =
        info.Arrays |> List.mapi (fun pos a ->
            match a with
            | IRRange _ | IRVirtualReverse _ when pos < info.ArrayTypes.Length ->
                max 1 (info.ArrayTypes.[pos].IndexTypes |> List.sumBy _.Rank)
            | IRRange _ | IRVirtualReverse _ -> refuse "a virtual apply operand without slot metadata"
            | _ -> 1)
    let paramStart pos = paramSpans |> List.take pos |> List.sum
    let inputRank pos = info.KernelInputRanks.[paramStart pos]
    if info.KernelInputRanks.Length <> List.sum paramSpans then
        refuse "an apply whose input-rank metadata does not match its operands"
    if info.KernelInputRanks |> List.exists (fun rank -> rank < 0) then
        refuse "an apply with negative input rank metadata"
    info.Arrays |> List.iteri (fun pos a ->
        match a with
        | IRRange _ | IRVirtualReverse _ ->
            if info.KernelInputRanks |> List.skip (paramStart pos) |> List.take paramSpans.[pos] |> List.exists ((<>) 0) then
                refuse "a virtual range/reverse passed as an array-valued kernel argument"
        | _ -> ())
    let cg =
        buildLoopNestCodeGen info
            (info.Arrays |> List.mapi (fun i _ -> $"__a{i}"))
            "__out" (IRBuilder())
    if List.isEmpty cg.Bindings then refuse "a combinator that produced no loop levels"
    for b in cg.Bindings do
        if b.FusedRank.IsSome then refuse "a fused joint loop level (compound index domain)"
    // THE COMPOUND VALUE MAP (G9): `y * y`, `method_for(y) <@> f` over a
    // rank-1 compound. ONE level whose extent is the compound's cardinality
    // (`IRCompoundMask`), every operand a real array of the SAME compound
    // (same kept-position table -- `compound(a, m)` and a map over it share
    // one mask by construction, which is what the C++ lane's shared-index
    // output `Compound { new T[card], y.idx, stride }` records). Cell k of
    // the result is the kernel of cell k of each operand, so this is the
    // cell-congruent producer over the same GCompact shape: no coordinates,
    // no storage until `|> compute` or a fold consumes it. Anything else
    // over a compound level -- a virtual source, two different compounds, a
    // Reynolds body, a row kernel -- refuses to the C++ lane.
    let compoundLevel =
        cg.Bindings |> List.exists (fun b -> match b.Extent with IRCompoundMask _ -> true | _ -> false)
    if compoundLevel then
        if cg.Bindings.Length <> 1 || cg.HasReynolds then
            refuse "a compound level inside a multi-level or Reynolds nest"
        let b = List.head cg.Bindings
        let operands =
            info.Arrays |> List.map (fun a ->
                match a with
                | IRRange _ | IRVirtualReverse _ -> refuse "a virtual source at a compound level"
                | _ -> emitArr c a)
        let shape =
            match operands with
            | first :: _ -> compactPosOf first
            | [] -> None
        let (len, pos) =
            match shape with
            | Some lp -> lp
            | None -> refuse "a compound level over an operand that is not a rank-1 compound"
        if not (operands |> List.forall (fun o -> compactPosOf o = Some (len, pos))) then
            refuse "a compound level over operands of different compounds"
        if b.Elements |> List.exists (fun el -> el.Virtual <> RealArray) then
            refuse "a compound level with a virtual element"
        let elem =
            match scalarTyOf rk.Callable.RetType with
            | Some s when s <> ScVoid -> s
            | _ ->
                match Blade.IR.stripUnits info.OutputType with
                | ArrayElem arr -> requireScalar "a compound map's element" arr.ElemType
                | t -> requireScalar "a compound map's result" t
        let outGroups = [ GCompact (len, pos) ]
        let paramOf (varId: IRId) = cg.KernelParams |> List.tryFind (fun p -> p.VarId = varId)
        { Elem = elem; Groups = outGroups; RowOpBytes = 0L
          Src = AVirtFlat (fun c2 k ->
            for el in b.Elements do
                let a = List.item el.ArrayPosition operands
                match paramOf el.ParamVarId with
                | Some p -> bindKernelParam c2 p (KScalar (readFlat c2 a k))
                | None -> refuse "a compound level with no kernel parameter"
            coerce c2 elem (emitExpr c2 cg.KernelExpr)) }
    else
    let extents =
        cg.Bindings |> List.map (fun b ->
            match Blade.IRPrint.tryEvalIntIR b.Extent with
            | Some n -> n
            | None -> refuse "a loop level with a runtime extent (the llvm lane bakes extents)")
    // THE OUTPUT'S STORAGE CLASS comes from the type the checker deduced, not
    // from the loop shape: a triangular NEST and a compact POOL are separate
    // facts (two different arrays in commuting positions iterate triangularly
    // and store densely -- the refusal the symmetry corpus pins). A symmetric
    // output type therefore decides the pool; anything without one keeps the
    // dense row-major shape the loop levels describe, which is what makes this
    // change invisible to every dense program.
    let outGroups =
        match Blade.IR.stripUnits info.OutputType with
        | ArrayElem arr when arr.IndexTypes |> List.exists (fun ix -> ix.Symmetry <> SymNone) ->
            let (_, groups) =
                requireArray "a combinator whose output storage is compact" info.OutputType
            if axisExtents groups <> extents then
                refuse "a compact output whose group extents disagree with the loop levels"
            groups
        | _ -> denseGroups extents
    // A triangular level is now emitted, not refused -- but ONLY when the
    // output is the compact pool that level is packing into. A triangular nest
    // writing a dense pool would leave the mirror half of it untouched
    // (uninitialized memory printed as values), so it stays a refusal.
    for b in cg.Bindings do
        if (not (List.isEmpty b.BoundDependencies) || b.StrictOffset <> 0)
           && isDenseShape outGroups then
            refuse "a triangular loop level over dense output storage"
    // The produced element type is the KERNEL's return type: a comparison
    // kernel consumes doubles and produces bools, and a broadcast kernel may
    // promote, exactly as genObjectForApplication reads it.
    let elem =
        match scalarTyOf rk.Callable.RetType with
        | Some s when s <> ScVoid -> s
        | _ ->
            match Blade.IR.stripUnits info.OutputType with
            | ArrayElem arr -> requireScalar "a combinator's element" arr.ElemType
            | t -> requireScalar "a combinator's result" t
    // Level -> which array positions peel there, and with which element
    // descriptor. Outer product gives one position per level; co-iteration
    // gives every position at every level.
    let levelsOf (pos: int) =
        cg.Bindings
        |> List.mapi (fun li b -> (li, b))
        |> List.collect (fun (li, b) ->
            b.Elements |> List.filter (fun el -> el.ArrayPosition = pos) |> List.map (fun el -> (li, el)))
    // method_for consumes an ArrayExpr BEFORE handing its rows to a kernel.
    // Letting a producer survive as an AVirt row view interleaves its cells
    // with row-kernel failures/writes (PM10/L1). Scalar-peel maps and virtual
    // scalar ranges retain their streaming path. Decide before emitting an
    // operand, so a call/extent computation is never evaluated twice.
    let rowOperands =
        info.Arrays |> List.mapi (fun pos a ->
            let kernelRank = inputRank pos
            match a with
            | IRRange _ | IRVirtualReverse _ -> false
            | _ ->
                let rank =
                    match Blade.IR.stripUnits (typeOf a) with
                    | ArrayElem at -> at.IndexTypes |> List.sumBy _.Rank
                    | _ -> refuse "a real apply operand without concrete array-rank metadata"
                let remaining = rank - (levelsOf pos).Length
                if remaining < 0 || (remaining > 0) <> (kernelRank > 0) then
                    refuse "an apply whose input rank disagrees with its actual operand peel"
                if paramStart pos >= rk.Callable.Params.Length then
                    refuse "a real apply operand without a kernel parameter"
                match Blade.IR.stripUnits rk.Callable.Params.[paramStart pos].Type with
                | ArrayElem at when at.IndexTypes.Length <> kernelRank ->
                    refuse "an apply whose input rank disagrees with its concrete kernel parameter"
                | ty when (scalarTyOf ty).IsSome && kernelRank <> 0 ->
                    refuse "an apply whose array input rank names a scalar kernel parameter"
                | _ -> ()
                remaining > 0)
    // Forcing a row operand matters only where the interleaving it prevents
    // could be OBSERVED: a producer that may panic or write, or a consuming
    // apply that writes. A closed-total, write-free scalar-cell producer over
    // stored operands, consumed by a write-free apply, keeps its AVirt row
    // views (one pass, no pool -- the pre-round-4 route): each cell is
    // evaluated once, in row order, and no program can tell when. Anything
    // unproved -- a producer this lane cannot see (a function-local deferred
    // let), a Reynolds kernel, a deferred operand of the producer -- is forced.
    let isDeferredId (id: IRId) =
        match c.ArrSlots.TryGetValue id with
        | true, v -> (match v.Src with AVirt _ | AVirtFlat _ -> true | _ -> false)
        | _ -> false
    let consumerWrites = lazy (Blade.CodeGenExprSupport.exprMayWriteIn c.BindingValues (IRApplyCombinator info))
    let deferredValue (id: IRId) =
        if not (isDeferredId id) then None
        else
            match Map.tryFind id c.BindingValues with
            | Some v -> Some v
            | None -> (match c.DeferredLetValues.TryGetValue id with | true, v -> Some v | _ -> None)
    let streamRow (a: IRExpr) =
        let producer =
            match a with
            | IRVar (id, _) -> deferredValue id
            | e -> Some e
        match producer with
        | Some (IRApplyCombinator p) when not p.HasReynolds && p.KernelOutputRank = 0 ->
            let storedOperand = function
                | IRRange _ | IRVirtualReverse _ -> true
                | IRVar (id, ty) -> not (isDeferredId id) && (match Blade.IR.stripUnits ty with ArrayElem at -> not at.IsVirtual | _ -> false)
                | _ -> false
            let res (k: IRExpr) = Blade.IR.resolveKernel k |> Option.map (fun r -> r.Callable)
            let dOf (id: IRId) = deferredValue id
            List.forall storedOperand p.Arrays
            && (match Blade.IRTotality.auditApply res dOf isDeferredId p with Ok () -> true | Error _ -> false)
            && not (Blade.CodeGenExprSupport.exprMayWriteIn c.BindingValues (IRApplyCombinator p))
            && not consumerWrites.Value
        | _ -> false
    let operands =
        info.Arrays |> List.mapi (fun pos a ->
            match a with
            | IRRange _ | IRVirtualReverse _ -> None
            | _ ->
                let value = if rowOperands.[pos] && not (streamRow a) then materializeExpr c a else emitArr c a
                let sourceRank =
                    match Blade.IR.stripUnits (typeOf a) with
                    | ArrayElem at -> at.IndexTypes |> List.sumBy _.Rank
                    | _ -> refuse "a real apply operand without concrete array-rank metadata"
                if value.Rank <> sourceRank || (value.Rank > (levelsOf pos).Length) <> rowOperands.[pos] then
                    refuse "an emitted apply operand whose rank disagrees with its kernel argument"
                Some value)
    let positions = cg.Bindings |> List.collect (fun b -> b.Elements |> List.map (_.ArrayPosition)) |> List.distinct
    let paramOf (varId: IRId) =
        cg.KernelParams |> List.tryFind (fun p -> p.VarId = varId)
    // A real array's subscript at a triangular level is the ABSOLUTE
    // coordinate: the loop counter is left-justified into its shrinking row,
    // so the array index is `counter + the prior levels it is measured from +
    // the strict step`. This is `genElementBindingPeel`'s case (1) verbatim
    // (CodeGenLoopNest.fs), and the two lanes must agree cell for cell.
    let absoluteAt (c2: Ctx) (li: int) (idxs: Val list) : Val =
        let b = cg.Bindings.[li]
        let mutable acc = idxs.[li]
        for d in b.BoundDependencies do
            acc <- addI64 c2 acc idxs.[d]
        if b.StrictOffset > 0 then addI64 c2 acc { Reg = string b.StrictOffset; Ty = ScI64 } else acc
    // The REYNOLDS term plan, computed once (it depends only on the kernel's
    // shape, not on the cell). Shared with the C++ lane down to the canonical
    // key -- `ReynoldsCore.reynoldsTermPlan` over `CodeGenLoopNest.canonicalKey`
    // is literally the same enumeration, dedup and ordering, so the two lanes
    // sum the same terms in the same order and their float bits agree.
    let reynoldsPlan =
        if not cg.HasReynolds then None
        else
            let n = List.length cg.KernelParams
            if n < 2 then None
            else
                let permNames (perm: int list) =
                    cg.KernelParams
                    |> List.mapi (fun i p -> (p.VarId, $"__perm{perm.[i]}"))
                    |> List.fold (fun acc (vid, nm) -> Map.add vid nm acc) Map.empty
                Some (Blade.ReynoldsCore.reynoldsTermPlan n cg.IsAntisymmetric
                        (fun perm -> Blade.CodeGenLoopNest.canonicalKey (permNames perm) cg.KernelExpr))
    // THE FLAT-ELEMENTWISE CASE (plan section 6, row 1: "already optimal --
    // do not touch this path"). When every operand is a real array of the
    // OUTPUT'S OWN SHAPE, fully peeled to scalars, with no virtual source and
    // no Reynolds permutation, the kernel sees cell values and nothing else:
    // cell k of the result is the kernel of cell k of each operand, and the
    // whole triangular apparatus -- row bases, shrinking bounds, block
    // decomposition -- is exactly the wasted motion the plan says not to
    // spend. A Reynolds body is excluded by name: it reads PERMUTED
    // coordinates, which is the opposite of index-free.
    let flatEligible =
        not (isDenseShape outGroups)
        && reynoldsPlan.IsNone
        && (operands |> List.forall Option.isSome)
        && (operands |> List.forall (fun o -> (Option.get o).Groups = outGroups))
        && (positions |> List.forall (fun pos ->
                let mine = levelsOf pos
                let a = Option.get (List.item pos operands)
                mine |> List.forall (fun (_, el) -> el.Virtual = RealArray)
                && List.length mine = List.length a.Extents))
    if flatEligible then
        { Elem = elem; Groups = outGroups
          RowOpBytes = 0L
          Src = AVirtFlat (fun c2 k ->
            for pos in positions do
                let a = Option.get (List.item pos operands)
                let mine = levelsOf pos
                match mine with
                | (_, el0) :: _ ->
                    (match paramOf el0.ParamVarId with
                     | Some p -> bindKernelParam c2 p (KScalar (readFlat c2 a k))
                     | None -> refuse "an array level with no kernel parameter")
                | [] -> ()
            coerce c2 elem (emitExpr c2 cg.KernelExpr)) }
    else
    // The reuse hint (plan-simplex-blocked-compute.md section 0, third block):
    // bytes of the largest operand a kernel parameter binds as a ROW VIEW in
    // the closure below. The predicate mirrors the KArray(rowView ...) site's
    // own test -- fewer peeled levels than the operand has extents -- and is
    // evaluated ONCE at construction, never per cell.
    let rowOpBytes =
        positions
        |> List.choose (fun pos ->
            match List.item pos operands with
            | Some a when List.length (levelsOf pos) < List.length a.Extents ->
                Some (shapeCells a.Groups * int64 (poolElemBytes a.Elem))
            | _ -> None)
        |> function [] -> 0L | xs -> List.max xs
    // TILE-CORE CANDIDACY (M6). The SAME matcher the C++ lane's
    // tryGenSyrkDispatch asks -- `LinAlgPatterns.(|BlasL3|_|)` over this
    // nest's shared loop description -- so the two lanes cannot disagree
    // about what a same-array comm row contraction is. Only the slot NAMES
    // differ: this lane names operand slots `__a0`, `__a1` even when both
    // are one array, so the pattern's same-array test is re-stated here on
    // the IR (the SAME binding id in both slots) and the second slot renamed
    // onto the first before asking. Whether to TAKE the tile (gates,
    // crossover) is materialize's call, made where the pool is built.
    let syrkCandidate : SyrkCandidate option =
        match info.Arrays, operands, outGroups, elem with
        | [ IRVar (id0, _); IRVar (id1, _) ], [ Some x; Some _ ], [ GSym (2, p) ], ScF64
                when id0 = id1 && reynoldsPlan.IsNone ->
            (match x.Elem, x.Groups with
             | ScF64, [ GDense p0; GDense k ] when p0 = p ->
                 let same (s: string) = if s = "__a1" then "__a0" else s
                 let cgSame =
                     { cg with
                         InputArrayNames = cg.InputArrayNames |> List.map same
                         Bindings =
                             cg.Bindings |> List.map (fun b ->
                                 { b with
                                     ExtentArrayRef = same b.ExtentArrayRef
                                     Elements = b.Elements |> List.map (fun e -> { e with ArrayName = same e.ArrayName }) }) }
                 let operandTypes =
                     info.Arrays |> List.choose (fun a ->
                         match Blade.IR.stripUnits (typeOf a) with
                         | ArrayElem t -> Some t
                         | _ -> None)
                 match (0, cg.OmpRequested, operandTypes, cgSame) with
                 | Blade.LinAlgPatterns.BlasL3 _ ->
                     (match cg.KernelExpr with
                      | Blade.LinAlgPatterns.ProdSumScaled (_, scale) ->
                          Some { Operand = x; P = p; K = k; Scale = scale }
                      | _ -> None)
                 | _ -> None
             | _ -> None)
        | _ -> None
    // ROW-CONTRACTION TILE CANDIDACY (M7 / M8): the antisym pair and the
    // rank-3 simplex, by the C++ lane's own `(|RowContraction|_|)` over this
    // nest's shared loop description -- the slot names folded onto `__a0`
    // exactly as for syrk, since every slot names the one operand. Whether to
    // TAKE the tile (knob, crossover, component proof) is materialize's call.
    let rowTileCandidate : RowTileCandidate option =
        let ids = info.Arrays |> List.map (function IRVar (id, _) -> Some id | _ -> None)
        let sameArray =
            match ids with
            | Some id0 :: rest when not rest.IsEmpty -> rest |> List.forall ((=) (Some id0))
            | _ -> false
        let firstOperand = match operands with Some x :: _ -> Some x | _ -> None
        match firstOperand, outGroups, elem with
        | Some x, ([ GAnti (2, _) ] | [ GSym (3, _) ]), ScF64
                when sameArray && reynoldsPlan.IsNone && x.Elem = ScF64
                     && operands |> List.forall Option.isSome ->
            let same (s: string) = if s.StartsWith "__a" && s <> "__a0" then "__a0" else s
            let cgSame =
                { cg with
                    InputArrayNames = cg.InputArrayNames |> List.map same
                    Bindings =
                        cg.Bindings |> List.map (fun b ->
                            { b with
                                ExtentArrayRef = same b.ExtentArrayRef
                                Elements = b.Elements |> List.map (fun e -> { e with ArrayName = same e.ArrayName }) }) }
            let operandTypes =
                info.Arrays |> List.choose (fun a ->
                    match Blade.IR.stripUnits (typeOf a) with
                    | ArrayElem t -> Some t
                    | _ -> None)
            match (0, operandTypes, cgSame) with
            | Blade.LinAlgPatterns.RowContraction rc ->
                (match rc.Shape, outGroups, x.Groups with
                 | Blade.LinAlgPatterns.AntiPair _, [ GAnti (2, p) ], [ GDense p0; GDense comps; GDense n ] when p0 = p ->
                     Some { Operand = x; Shape = rc.Shape; P = p; Comps = comps; N = n; Scale = rc.Scale }
                 | Blade.LinAlgPatterns.Sym3, [ GSym (3, p) ], [ GDense p0; GDense n ] when p0 = p ->
                     Some { Operand = x; Shape = rc.Shape; P = p; Comps = 0L; N = n; Scale = rc.Scale }
                 | _ -> None)
            | _ -> None
        | _ -> None
    { Elem = elem; Groups = outGroups
      RowOpBytes = rowOpBytes
      Src = AVirt (fun c2 idxs ->
        // Every kernel parameter's argument, in parameter order, so a Reynolds
        // sum can re-bind them under a permutation without re-reading a cell.
        let bound = Dictionary<IRId, KArg>()
        let bind (p: IRParam) (arg: KArg) =
            bound.[p.VarId] <- arg
            bindKernelParam c2 p arg
        for pos in positions do
            let mine = levelsOf pos
            match mine with
            | [] -> ()
            | (_, el0) :: _ ->
                match el0.Virtual with
                // VIRTUAL SOURCES BIND PER SLOT, and the offset is per slot
                // too: `range<halo<Lat, ..>, Lon>` gives the halo slot the
                // window's start offset and the plain slot none. Reading el0's
                // offset for every level of the source shifts the plain axis
                // by the halo's interior shrink -- the 2-D separable stencil
                // then reads one column over, silently.
                | VirtualRange _ | VirtualReverse ->
                    for (li, el) in mine do
                        let v =
                            match el.Virtual with
                            | VirtualRange offset ->
                                let off =
                                    match offset with
                                    | Some o -> Blade.IRPrint.tryEvalIntIR o |> Option.defaultValue 0L
                                    | None -> 0L
                                addI64 c2 idxs.[li] { Reg = string off; Ty = ScI64 }
                            | VirtualReverse ->
                                let d = freshReg c2
                                ln c2 (renderBin { Dest = d; Opcode = "sub"; Flags = ""; Ty = ScI64
                                                   Lhs = string (extents.[li] - 1L); Rhs = idxs.[li].Reg })
                                { Reg = d; Ty = ScI64 }
                            | RealArray -> refuse "a mixed virtual/real source at one array position"
                        match paramOf el.ParamVarId with
                        | Some p -> bind p (KScalar v)
                        | None -> refuse "a virtual level with no kernel parameter"
                | RealArray ->
                    let a =
                        match List.item pos operands with
                        | Some a -> a
                        | None -> refuse "a real-array level over a virtual source"
                    // WHICH COORDINATE SYSTEM this operand wants. A DENSE
                    // array is read at the absolute coordinate; a COMPACT one
                    // is read at its STORAGE coordinate, which is the raw
                    // left-justified loop counter -- adding the dependency
                    // back would walk off the shrinking row. This is the same
                    // split `genElementBindingPeel` makes between its
                    // "original array" and "already-sliced sub-array" cases:
                    // peeling a compact pool row by row leaves the inner
                    // subscript local, and reading the pool in one step (as
                    // this lane does) means never shifting it in the first
                    // place.
                    let rawAxis =
                        [ for g in a.Groups do
                            for k in 0 .. grpRank g - 1 -> (grpCompact g && k > 0) ]
                    let myIdxs =
                        mine |> List.mapi (fun d (li, _) ->
                            if d < rawAxis.Length && rawAxis.[d] then idxs.[li]
                            else absoluteAt c2 li idxs)
                    let arg =
                        if (List.length myIdxs < a.Rank) <> rowOperands.[pos] then
                            refuse "an apply whose scalar/array kernel argument disagrees with its input-rank metadata"
                        if List.length myIdxs = List.length a.Extents then KScalar (readCell c2 a myIdxs)
                        elif List.length myIdxs < List.length a.Extents then KArray (rowView c2 a myIdxs)
                        else refuse "a loop nest that peels more levels than the array has"
                    match paramOf el0.ParamVarId with
                    | Some p -> bind p arg
                    | None -> refuse "an array level with no kernel parameter"
        match reynoldsPlan with
        | None -> coerce c2 elem (emitExpr c2 cg.KernelExpr)
        | Some plan ->
            // sum_perm coeff * kernel(args permuted), left-associated in the
            // plan's first-occurrence order -- and, when the plan is empty,
            // ZERO: antisymmetrizing a symmetric kernel cancels completely,
            // and the C++ lane spells that `0.0` too.
            let args =
                cg.KernelParams
                |> List.map (fun p ->
                    match bound.TryGetValue p.VarId with
                    | true, a -> a
                    | _ -> refuse "a reynolds kernel whose parameters are not all bound by the nest")
            let termOf (perm: int list) =
                List.iteri (fun i (p: IRParam) -> bindKernelParam c2 p args.[perm.[i]]) cg.KernelParams
                coerce c2 elem (emitExpr c2 cg.KernelExpr)
            let scaled (coeff: int) (v: Val) =
                if coeff = 1 then v
                else
                    match elem with
                    | ScF64 ->
                        let d = freshReg c2
                        ln c2 (renderBin { Dest = d; Opcode = "fmul"; Flags = fmfFor c2 ScF64; Ty = ScF64
                                           Lhs = f64Const (float coeff); Rhs = v.Reg })
                        { Reg = d; Ty = ScF64 }
                    | ScI64 -> { Reg = i64Bin c2 "mul" (string coeff) v.Reg; Ty = ScI64 }
                    | _ -> refuse "a reynolds sum over non-numeric elements"
            match plan.Terms with
            | [] -> { Reg = (match elem with ScF64 -> f64Const 0.0 | _ -> "0"); Ty = elem }
            | (c0, p0) :: rest ->
                let mutable acc = scaled c0 (termOf p0)
                for (coeff, perm) in rest do
                    let t = termOf perm
                    // A negative antisymmetric coefficient is a SUBTRACTION,
                    // not an add of a negated term: `a - b`, which is the
                    // expression the C++ lane emits and the same bits.
                    let (opcode, rhs) =
                        if coeff < 0 then
                            ((if elem = ScF64 then "fsub" else "sub"), scaled (abs coeff) t)
                        else ((if elem = ScF64 then "fadd" else "add"), scaled coeff t)
                    let d = freshReg c2
                    ln c2 (renderBin { Dest = d; Opcode = opcode
                                       Flags = (if elem = ScF64 then fmfFor c2 ScF64 else "")
                                       Ty = elem; Lhs = acc.Reg; Rhs = rhs.Reg })
                    acc <- { Reg = d; Ty = elem }
                acc) }
    |> fun (produced: ArrVal) ->
        (match syrkCandidate with
         | Some cand -> syrkCandidates.AddOrUpdate(produced, cand)
         | None -> ())
        (match rowTileCandidate with
         | Some cand -> rowTileCandidates.AddOrUpdate(produced, cand)
         | None -> ())
        produced

/// The RAGGED PEEL: `method_for(r) <@> kernel` over a ragged pair, dispatched
/// on the OUTPUT type exactly as the C++ lane's tryRaggedPeel dispatches
/// (CodeGenCuda.fs): an output that KEEPS the ragged inner axis is the
/// shape-preserving ELEMENTWISE map (the kernel sees one scalar cell, and the
/// output shares the operand's offsets, so the whole thing is cell-congruent
/// -- the flat path, zero coordinate arithmetic); an output that DROPS it is
/// the CONSUMING form (the kernel sees the row view, answers one scalar per
/// row, and the output is a plain dense vector). Both stay producers;
/// `|> compute` materializes as usual.
and private applyRaggedPeel (c: Ctx) (info: ApplyInfo) (cl: IRCallable) : ArrVal =
    match info.Arrays, cl.Params with
    | [ arrExpr ], [ _ ] ->
        // The operand rule is emitReduce's three-way rule: a multi-read name
        // forces so readers share one pool; a sole-read module binding is
        // consumed but marked forced so it still prints; anything else is
        // consumed where it stands.
        let a = consumeArrOperand c arrExpr
        let rows =
            match a.Groups with
            | [ GRagged (rows, _) ] -> rows
            | _ -> refuse "a ragged peel whose operand is not a sole ragged pair"
        // Snapshot the row count OUTSIDE the producer closure: a grouped
        // operand's count is a register, and the closure must not re-emit
        // whatever computed it.
        let outputRagged =
            match Blade.IR.stripUnits info.OutputType with
            | ArrayElem arr -> arr.IndexTypes |> List.exists (fun ix -> isRaggedRowKind ix.IxKind)
            | _ -> false
        let elem =
            match scalarTyOf cl.RetType with
            | Some s when s <> ScVoid -> s
            | _ ->
                match Blade.IR.stripUnits info.OutputType with
                | ArrayElem arr -> requireScalar "a ragged combinator's element" arr.ElemType
                | t -> requireScalar "a ragged combinator's result" t
        if outputRagged then
            if not (hasFlatRead a) then
                refuse "an elementwise ragged map over a coordinate-bearing producer"
            { Elem = elem; Groups = a.Groups; RowOpBytes = 0L
              Src = AVirtFlat (fun c2 k ->
                        coerce c2 elem (applyKernel c2 cl [ KScalar (readFlat c2 a k) ])) }
        else
            { Elem = elem; Groups = [ denseOfOp rows ]; RowOpBytes = 0L
              Src = AVirt (fun c2 idxs ->
                        match idxs with
                        // the peel kernel's body runs inlined, no frame (the
                        // C++ lane's inlined peel; gap G76)
                        | [ g ] -> coerce c2 elem (applyKernelFramed c2 false cl [ KArray (rowView c2 a [ g ]) ])
                        | _ -> refuse "a ragged peel read at other than its row coordinate") }
    | arrays, ps ->
        refuse ($"a ragged combinator over {(List.length arrays)} arrays and {(List.length ps)} kernel parameters -- mixing ragged operands with other arrays or multi-parameter kernels is not supported")

/// `object_for(k)` applied directly to arrays -- the shape `A + B`, `A * 2.0`
/// and `A [+] B` lower to (Lowering.lowerTypedBinOp). InputRanks says which:
/// `[0; 0]` co-iterates, `[1; 1]` is the outer product, `[0]` is the
/// one-array broadcast whose scalar operand is already baked into the kernel.
and private objectForApply (c: Ctx) (objInfo: ObjectForInfo) (args: IRExpr list) (retType: IRType) : ArrVal =
    let cl =
        match resolveCallable objInfo.Kernel with
        | Some cl -> cl
        | None -> refuse "an object_for whose kernel does not resolve to a callable"
    let arrays =
        match args with
        | [ IRTuple elems ] -> elems
        | _ -> args
    let ops = arrays |> List.map (emitArr c)
    let elem =
        match scalarTyOf cl.RetType with
        | Some s when s <> ScVoid -> s
        | _ ->
            match Blade.IR.stripUnits retType with
            | ArrayElem arr -> requireScalar "an object_for result element" arr.ElemType
            | t -> requireScalar "an object_for result" t
    // CO-ITERATION OVER ONE SHAPE IS CELL-CONGRUENT: cell k of the result is
    // the kernel of cell k of each operand, whatever the shape means. On a
    // compact pool that is the flat-elementwise path -- one counted loop, zero
    // coordinate arithmetic -- and the blocked-simplex plan is explicit that
    // this path is already optimal and must not be bricked (section 6, first
    // row: "do not touch this path"). A dense shape keeps the coordinate
    // producer it always had, so dense emission does not move.
    let congruentFlat (parts: ArrVal list) =
        parts |> List.forall hasFlatRead
        && (parts |> List.exists (fun p ->
                p.Groups |> List.exists (fun g -> grpCompact g || isRaggedGrp g)))
    match objInfo.InputRanks, ops with
    | [ 0; 0 ], [ a; b ] ->
        if a.Groups <> b.Groups then
            refuse "an elementwise operator over arrays of different static shapes"
        if congruentFlat [ a; b ] then
            { Elem = elem; Groups = a.Groups
              RowOpBytes = 0L
              Src = AVirtFlat (fun c2 k ->
                        coerce c2 elem (applyKernel c2 cl [ KScalar (readFlat c2 a k); KScalar (readFlat c2 b k) ])) }
        else
        { Elem = elem; Groups = a.Groups
          RowOpBytes = 0L
          Src = AVirt (fun c2 idxs ->
                    coerce c2 elem (applyKernel c2 cl [ KScalar (readCell c2 a idxs); KScalar (readCell c2 b idxs) ])) }
    | [ 1; 1 ], [ a; b ] ->
        if (a.Groups @ b.Groups) |> List.exists (fun g -> grpCompact g || isRaggedGrp g) then
            refuse "an outer product over compact or ragged storage"
        let ra = a.Rank
        { Elem = elem; Groups = a.Groups @ b.Groups
          RowOpBytes = 0L
          Src = AVirt (fun c2 idxs ->
                    let ia = idxs |> List.truncate ra
                    let ib = idxs |> List.skip ra
                    coerce c2 elem (applyKernel c2 cl [ KScalar (readCell c2 a ia); KScalar (readCell c2 b ib) ])) }
    | [ 0 ], [ a ] ->
        if congruentFlat [ a ] then
            { Elem = elem; Groups = a.Groups
              RowOpBytes = 0L
              Src = AVirtFlat (fun c2 k ->
                        coerce c2 elem (applyKernel c2 cl [ KScalar (readFlat c2 a k) ])) }
        else
        { Elem = elem; Groups = a.Groups
          RowOpBytes = 0L
          Src = AVirt (fun c2 idxs ->
                    coerce c2 elem (applyKernel c2 cl [ KScalar (readCell c2 a idxs) ])) }
    | ranks, _ ->
        refuse (sprintf "an object_for application with input ranks %A over %d arrays" ranks (List.length ops))

// ---------------------------------------------------------------------------
// Reductions
// ---------------------------------------------------------------------------

/// `reduce(A, op[, init])`. ORDER-PRESERVING and SERIAL: seed from element 0
/// (or `init`), then accumulate ASCENDING, left-associated -- the exact walk
/// `Interp/ArrayOps.reduceArray` performs, which is what the differential gate
/// compares against. No reassociation, licensed or not: FMF arrives with the
/// fact layer, not here.
and private emitReduce (c: Ctx) (arrExpr: IRExpr) (kernelExpr: IRExpr) (initExpr: IRExpr option) : Val =
    // G41: an ordinary fold consumes its operand too -- a virtual producer
    // (a choice / guard over a writing map, a named deferred map read inside
    // an inlined kernel) runs inside this fold, so its writes keep the fold's
    // serial association here exactly as in emitReduceCompute.
    let saved = c.FoldOperandWrites
    c.FoldOperandWrites <- saved || Blade.CodeGenExprSupport.exprMayWriteIn c.BindingValues arrExpr
    try emitReduceCore c arrExpr kernelExpr initExpr
    finally c.FoldOperandWrites <- saved

and private emitReduceCore (c: Ctx) (arrExpr: IRExpr) (kernelExpr: IRExpr) (initExpr: IRExpr option) : Val =
    // The operand is CONSUMED, not forced. `readCell` runs a producer
    // natively -- the header's contract, "reduce over an unforced computation
    // is run the producer inside the fold with no array in between" -- and
    // the fold below reads each cell exactly once, so an anonymous deferred
    // operand (`reduce(x * y, (+))` in a kernel body: the gram fixture's
    // per-evaluation alloc-fill-refold, ~14.5 GB across one run's cells)
    // folds in ONE pass with no temp at all.
    //
    // A NAMED operand still forces, deliberately: `materializeExpr` upgrades
    // the binding's slot so every LATER read shares the pool, and consuming
    // a named producer lazily here would re-run its kernel once per
    // consumer -- the units/065 double-pass, reintroduced backwards. `IRVar`
    // is the naming boundary, so it is the guard.
    //
    // COMPACTION FUSION (Blade.Optimize.planCompactionFusion): the fold of a
    // planned `y * y` whose chain the compaction pass already fed (B4c) is
    // the empty check at this declaration plus that accumulator; one it did
    // not feed consumes the map let as a producer (B4a, bound unforced by
    // bindNamed) -- the fold below then reads `y`'s pool through the map.
    match arrExpr, initExpr with
    | IRVar (mid, _), None when (match Blade.Optimize.CompactionTable.tryFoldMap mid with
                                 | Some p ->
                                     (match c.ArrSlots.TryGetValue p.CompoundId with
                                      | true, { Src = APool _; Elem = ScF64; Groups = [ GCompact _ ] } -> true
                                      | _ -> false)
                                 | None -> false) ->
        let p = (Blade.Optimize.CompactionTable.tryFoldMap mid).Value
        let y = c.ArrSlots.[p.CompoundId]
        let len = soleExtentOp y
        let isEmpty = freshReg c
        ln c ($"{isEmpty} = icmp eq i64 {len}, 0")
        let lEmpty = freshLbl c "reduce.empty"
        let lOk = freshLbl c "reduce.ok"
        ln c (renderBr { Cond = isEmpty; IfTrue = lEmpty; IfFalse = lOk })
        lbl c lEmpty
        needShim c "blade_panic_at"
        let msg = stringGlobal c "error[BL8003]: reduce: empty array, no reduction possible"
        ln c (renderCall { Dest = None; RetTy = ScVoid; Callee = "@blade_panic_at"; Args = [ ScStr, msg ] @ declLocArgs c })
        ln c "unreachable"
        lbl c lOk
        match p.EmittedLlvm.Value with
        | Some acc -> loadSlot c acc ScF64
        | None ->
        // B4a alone: the unseeded fold over y's pool, the self-product
        // emitted with NO fast-math flag -- it keeps the rounding a stored
        // `y * y` cell had (the materialized map's store was a contraction
        // boundary; an undecorated `fmul` cannot fuse into the add either).
        // The fold's own `+` carries this build's licence flags, as before.
        let cl =
            match resolveCallable kernelExpr with
            | Some cl when cl.Params.Length = 2 -> cl
            | _ -> refuse "a compaction fold whose kernel does not resolve to a two-parameter callable"
        let pool = match y.Src with APool ptr -> ptr | _ -> refuse "unreachable: compound not stored"
        let square (j: string) : Val =
            let v = loadCell c ScF64 (gepCell c ScF64 pool j)
            let r = freshReg c
            ln c (renderBin { Dest = r; Opcode = "fmul"; Flags = ""; Ty = ScF64; Lhs = v.Reg; Rhs = v.Reg })
            { Reg = r; Ty = ScF64 }
        let acc = allocaOf c (llTy ScF64)
        let v0 = square "0"
        ln c ($"store double {v0.Reg}, ptr {acc}")
        let licensed = foldFmfDecorable c cl
        emitCountedLoopTo c (i64Bin c "sub" len "1") (fun k ->
            let p1 = square (i64Add c k "1")
            let cur = loadSlot c acc ScF64
            let next = withFoldFmf c licensed (fun () -> coerce c ScF64 (applyKernel c cl [ KScalar cur; KScalar p1 ]))
            ln c ($"store double {next.Reg}, ptr {acc}"))
        loadSlot c acc ScF64
    | _ ->
    let a =
        match arrExpr with
        | IRVar (id, _) when (Blade.Optimize.CompactionTable.tryFoldMap id).IsSome -> emitArr c arrExpr
        // A name read MORE than once must force: `materializeExpr` upgrades
        // the slot so every reader shares one pool. A SOLE-READ name -- the
        // IRLift hoist that gives a kernel-body operand its buffer name,
        // whose only reader is this fold -- has nobody to share with, so it
        // is consumed like the anonymous case (`ModuleFacts.ReadOnce`).
        //
        | IRVar (id, _) when not (c.Facts.ReadOnce.Contains id) -> materializeExpr c arrExpr
        // A SOLE-READ MODULE BINDING: consume it, but still mark it forced.
        // The two things `materializeExpr` bundles come apart here. Storage
        // is not wanted -- this fold may be emitted inside a loop nest, and
        // a pool allocated there does not dominate the print that reads it
        // after ("Instruction does not dominate all uses", which is what
        // materializing unconditionally actually produced). PRINTABILITY is
        // wanted: `emitPrints` shows a deferred binding only if `Forced`
        // holds it, so consuming without the mark silently deletes an output
        // line the C++ lane emits (`c = [2, 4, 6, 8]`, corpus loops/193).
        // `markForced` records printability, NOT storage -- the printer
        // re-runs the producer at module scope, where nothing encloses it.
        // Where the force can be hoisted to the statement's start it is
        // stored there instead (gap G60 (b), soleReadModuleOperand), so the
        // print reads the first-forcing cells.
        | IRVar (id, _) when c.BindingValues.ContainsKey id ->
            // the retention certificate: no assignment anywhere, a total
            // fold kernel and seed (IRTotality's closed whitelist)
            // The DEFERRED facts go to the audit too (review
            // codex-diff-W4B-B-2 finding 2): a kernel or seed that reads an
            // array this lane still holds as a producer -- a deferred map
            // whose own evaluation (and failure) the whitelist cannot see --
            // is not certified, so the operand is stored first.
            let consumerTotal () =
                let noDeferred (_: IRId) : IRExpr option = None
                let isDeferred (vid: IRId) =
                    match c.ArrSlots.TryGetValue vid with
                    | true, { Src = APool _ } -> false
                    | true, _ -> true
                    | _ -> false
                let kOk =
                    match resolveCallable kernelExpr with
                    | Some kcl -> (Blade.IRTotality.auditCallable resolveCallable noDeferred isDeferred kcl []) |> Result.isOk
                    | None -> false
                let sOk =
                    match initExpr with
                    | None -> true
                    | Some s -> (Blade.IRTotality.walk resolveCallable noDeferred isDeferred Map.empty Map.empty Set.empty Set.empty Set.empty s) |> Result.isOk
                kOk && sOk
            soleReadModuleOperand c id arrExpr (not c.Facts.AnyAssignment && consumerTotal ())
        | _ -> emitArr c arrExpr
    let cl =
        match resolveCallable kernelExpr with
        | Some cl when cl.Params.Length = 2 -> cl
        | Some cl -> refuse ($"a fold kernel '{cl.Name}' of {cl.Params.Length} parameters")
        | None -> refuse "a reduce whose kernel does not resolve to a callable"
    let accTy =
        match scalarTyOf cl.RetType with
        | Some s when s <> ScVoid -> s
        | _ -> a.Elem
    if a.Groups |> List.exists grpCompact then emitCompactFold c a cl accTy initExpr else
    match a.Groups with
    | [ GDomain plan ] ->
        // A domain array (materialized or not) folds WHOLE, its cells in lex
        // order -- `reduce` over the sparse-shaped array is the flat walk in
        // both other lanes, and lex order IS pool order.
        let acc = allocaOf c (llTy accTy)
        let seeded =
            match initExpr with
            | Some ini ->
                let v = coerce c accTy (emitExpr c ini)
                ln c ($"store {(llTy accTy)} {v.Reg}, ptr {acc}")
                true
            | None ->
                if plan.Card < 1L then refuse "reduce over a statically empty domain"
                false
        let have = allocaOf c "i1"
        let seededLit = if seeded then "true" else "false"
        ln c ($"store i1 {seededLit}, ptr {have}")
        let licensed = foldFmfDecorable c cl
        // THE ORDER: the C++ lane's decision (CodeGenExprSupport.domainFoldOrder),
        // so a band under the licence walks by diagonal in both lanes. A seeded
        // fold over the VIRTUAL map only (a pool folds in pool order).
        let diagonal =
            match a.Src with
            | AVirt produce when seeded && licensed ->
                let body = match c.DomainMapBodies.TryGetValue (box produce) with | true, b -> Some b | _ -> None
                (match Blade.CodeGenExprSupport.domainFoldOrder (Some cl) body plan with
                 | Blade.CodeGenExprSupport.DomainDiagonalOrder dg -> Some dg
                 | Blade.CodeGenExprSupport.DomainLexOrder _ -> None)
            | _ -> None
        match diagonal with
        | Some dg ->
            emitDomainDiagonalNest c dg (fun idxs ->
                let cell = readCell c a idxs
                let cur = loadSlot c acc accTy
                let next = withFoldFmf c licensed (fun () -> coerce c accTy (applyKernel c cl [ KScalar cur; KScalar cell ]))
                ln c ($"store {(llTy accTy)} {next.Reg}, ptr {acc}"))
            loadSlot c acc accTy
        | None ->
        emitDomainNest c plan (fun idxs off ->
            let cell =
                match a.Src with
                | APool _ -> readFlat c a off
                | _ -> readCell c a idxs
            if seeded then
                let cur = loadSlot c acc accTy
                let next = withFoldFmf c licensed (fun () -> coerce c accTy (applyKernel c cl [ KScalar cur; KScalar cell ]))
                ln c ($"store {(llTy accTy)} {next.Reg}, ptr {acc}")
            else
                // Unseeded: the first cell seeds (a left fold from cell 0).
                let h = freshReg c
                ln c ($"{h} = load i1, ptr {have}")
                let lSeed = freshLbl c "dfold.seed"
                let lStep = freshLbl c "dfold.step"
                let lJoin = freshLbl c "dfold.join"
                ln c (renderBr { Cond = h; IfTrue = lStep; IfFalse = lSeed })
                lbl c lSeed
                let v0 = coerce c accTy cell
                ln c ($"store {(llTy accTy)} {v0.Reg}, ptr {acc}")
                ln c ($"store i1 true, ptr {have}")
                ln c (sprintf "br label %%%s" lJoin)
                lbl c lStep
                let cur = loadSlot c acc accTy
                let next = withFoldFmf c licensed (fun () -> coerce c accTy (applyKernel c cl [ KScalar cur; KScalar cell ]))
                ln c ($"store {(llTy accTy)} {next.Reg}, ptr {acc}")
                ln c (sprintf "br label %%%s" lJoin)
                lbl c lJoin)
        loadSlot c acc accTy
    | _ ->
    if a.Rank <> 1 then
        refuse "reduce over a rank-2+ array (partial folds are not in the llvm lane yet)"
    // The bound is an OPERAND: a literal for a static axis (byte-identical to
    // the counted loop this always was), the carried length for a peeled
    // ragged row. An unseeded fold over a statically empty row refuses; over
    // a RUNTIME extent it is guarded, BL8003 at the declaration being emitted
    // -- the C++ lane's genReduceBinding / renderReduceExpr guard.
    let bound = soleExtentOp a
    let acc = allocaOf c (llTy accTy)
    let start =
        match initExpr with
        | Some ini ->
            let v = coerce c accTy (emitExpr c ini)
            ln c ($"store {(llTy accTy)} {v.Reg}, ptr {acc}")
            0L
        | None ->
            (match tryLitI64 bound with
             | Some n when n < 1L -> refuse "reduce over a statically empty array"
             | Some _ -> ()
             | None ->
                 let isEmpty = freshReg c
                 ln c ($"{isEmpty} = icmp eq i64 {bound}, 0")
                 let lEmpty = freshLbl c "reduce.empty"
                 let lOk = freshLbl c "reduce.ok"
                 ln c (renderBr { Cond = isEmpty; IfTrue = lEmpty; IfFalse = lOk })
                 lbl c lEmpty
                 needShim c "blade_panic_at"
                 let m = stringGlobal c "error[BL8003]: reduce: empty array, no reduction possible"
                 ln c (renderCall { Dest = None; RetTy = ScVoid; Callee = "@blade_panic_at"; Args = [ ScStr, m ] @ declLocArgs c })
                 ln c "unreachable"
                 lbl c lOk)
            let v0 = coerce c accTy (readCell c a [ { Reg = "0"; Ty = ScI64 } ])
            ln c ($"store {(llTy accTy)} {v0.Reg}, ptr {acc}")
            1L
    let licensed = foldFmfDecorable c cl
    let trips =
        if start = 0L then bound
        else
            match tryLitI64 bound with
            | Some n -> string (n - start)
            | None -> i64Bin c "sub" bound (string start)
    emitCountedLoopTo c trips (fun k ->
        let i = addI64 c { Reg = k; Ty = ScI64 } { Reg = string start; Ty = ScI64 }
        let cur = loadSlot c acc accTy
        // The cell read is OUTSIDE the licensed scope on purpose: on a deferred
        // operand it runs the map kernel, whose arithmetic carries no fold
        // license (see `withFoldFmf`).
        let cell = readCell c a [ i ]
        let next =
            withFoldFmf c licensed (fun () ->
                coerce c accTy (applyKernel c cl [ KScalar cur; KScalar cell ]))
        ln c ($"store {(llTy accTy)} {next.Reg}, ptr {acc}"))
    loadSlot c acc accTy

/// A fold over a COMPACT (simplex) domain: every canonical cell, once.
///
/// THE BLOCKED-SIMPLEX FOLD (docs/plans/plan-simplex-blocked-compute.md
/// sections 4a and 7). Two forms, and which one is emitted is a LICENSING
/// question, not a performance one:
///
///   * UNLICENSED -- the ordinary serial canonical walk, cells in pool order,
///     left-associated. Byte-identical to what a flat traversal produces,
///     which is what an unlicensed fold is entitled to.
///   * LICENSED (`foldFmfLicensed`: the kernel is `comm`-declared or is a
///     recognized commutative builtin -- the same structural predicate the
///     C++ lane spends on its K-lane forms and its OMP chunking -- in any
///     build but BLADE_FP_REPRO=1, because the brick walk reassociates
///     the RESULT) -- the brick decomposition, with ONE PARTIAL PER BRICK
///     combined into the total in ascending-lex block order. Deterministic-but-reassociated: the same
///     program always produces the same bits, and those bits may differ from
///     the serial walk exactly as far as the declared commutativity says they
///     may. Each brick seeds from its OWN first contributed value (the `have`
///     flag), so no identity element is required and an empty brick
///     contributes nothing -- the discipline `FoldChunkPlan` already uses.
///
/// REACHABILITY, stated plainly: no Blade program can currently deliver a
/// compact operand to a fold. `reduce()` over compact symmetric/antisymmetric
/// storage -- and over a computation whose OUTPUT is compact -- is a FRONT-END
/// refusal at five sites in TypeCheckInfer (BL3999: "folding the canonical
/// cells and folding the logical (mirrored) cells differ ... decompact(A, d)
/// first"), because the surface has never decided which of the two folds
/// `reduce` means. So this arm is the back end holding up its end of a
/// contract the front end has not signed: the nest it lays down is the same
/// `emitSimplex2` the compact MAP path runs on every symmetric corpus file,
/// and only the accumulator plumbing here is unexercised.
and private emitCompactFold (c: Ctx) (a: ArrVal) (cl: IRCallable) (accTy: Sc) (initExpr: IRExpr option) : Val =
    let total = shapeCells a.Groups
    if total < 1L then refuse "a fold over a statically empty compact domain"
    let acc = allocaOf c (llTy accTy)
    // The brick decomposition REASSOCIATES THE RESULT (one partial per brick,
    // combined in block order), so it takes the same two-part gate as FMF:
    // the structural license AND the build's polarity. Structure alone must
    // never brick in a REPRO build -- under BLADE_FP_REPRO=1 the serial
    // canonical walk is the only walk, which is what keeps the byte-identity
    // oracles (which pin that build) honest the day BL3999's refusal lifts.
    let brickable = foldFmfLicensed c cl
    let fmf = foldFmfDecorable c cl
    let combine (dst: string) (cur: Val) (cell: Val) =
        let next = withFoldFmf c fmf (fun () -> coerce c accTy (applyKernel c cl [ KScalar cur; KScalar cell ]))
        ln c ($"store {(llTy accTy)} {next.Reg}, ptr {dst}")
    // The seed. Cell 0 in POOL order is also the first cell in BLOCK order
    // (block (0,0) comes first, and its first row is row 0), so an unseeded
    // fold starts from the same value either way -- and every remaining cell
    // is then folded in, once.
    let seeded =
        match initExpr with
        | Some ini ->
            let v = coerce c accTy (emitExpr c ini)
            ln c ($"store {(llTy accTy)} {v.Reg}, ptr {acc}")
            true
        | None -> false
    let haveTotal = allocaOf c "i1"
    ln c ($"""store i1 {(if seeded then "true" else "false")}, ptr {haveTotal}""")
    let feed (dst: string) (haveFlag: string) (cell: Val) =
        let h = freshReg c
        ln c ($"{h} = load i1, ptr {haveFlag}")
        let lHave = freshLbl c "fold.have"
        let lFirst = freshLbl c "fold.first"
        let lEnd = freshLbl c "fold.done"
        ln c (renderBr { Cond = h; IfTrue = lHave; IfFalse = lFirst })
        lbl c lHave
        combine dst (loadSlot c dst accTy) cell
        ln c (sprintf "br label %%%s" lEnd)
        lbl c lFirst
        let v0 = coerce c accTy cell
        ln c ($"store {(llTy accTy)} {v0.Reg}, ptr {dst}")
        ln c ($"store i1 true, ptr {haveFlag}")
        ln c (sprintf "br label %%%s" lEnd)
        lbl c lEnd
    match soleSimplexR a.Groups with
    | None ->
        // A compact group beside other groups: serial over the whole shape.
        emitShapeNest c a.Groups (fun idxs _ -> feed acc haveTotal (readCell c a idxs))
        loadSlot c acc accTy
    | Some (r, n, strict) ->
        // Folds carry no reuse hint: the canonical fold streams the pool once
        // (nothing to re-read), so bricking stays knob+licence-only here --
        // and the row-bin schedule in plan-compact-sym-folds.md section 5.6
        // is the fold's real future, not bricks.
        match (if r = 2 then brickTileEdge brickable 0L n else None) with
        | None ->
            // Serial canonical order. When the operand can answer a flat read
            // (a pool, or a cell-congruent producer) that order IS pool order,
            // so the walk is one counted loop with no coordinate arithmetic --
            // and that shortcut is rank-independent, which is why a rank-3
            // fold over a materialized pool costs exactly what a rank-2 one
            // does per cell.
            if hasFlatRead a then
                emitCountedLoop c total (fun k -> feed acc haveTotal (readFlat c a { Reg = k; Ty = ScI64 }))
            else
                emitSimplexR c r n strict None (fun emit -> emit ())
                    (fun coords _ -> feed acc haveTotal (readCell c a coords))
            loadSlot c acc accTy
        | Some b ->
            let part = allocaOf c (llTy accTy)
            let havePart = allocaOf c "i1"
            emitSimplexR c r n strict (Some b)
                (fun emitBlock ->
                    ln c ($"store i1 false, ptr {havePart}")
                    emitBlock ()
                    // The brick's partial joins the total HERE, so the combine
                    // order is exactly `SimplexBlocksCore.blockSequence`.
                    let hp = freshReg c
                    ln c ($"{hp} = load i1, ptr {havePart}")
                    let lJoin = freshLbl c "brick.join"
                    let lSkip = freshLbl c "brick.skip"
                    ln c (renderBr { Cond = hp; IfTrue = lJoin; IfFalse = lSkip })
                    lbl c lJoin
                    feed acc haveTotal (loadSlot c part accTy)
                    ln c (sprintf "br label %%%s" lSkip)
                    lbl c lSkip)
                (fun coords _ -> feed part havePart (readCell c a coords))
            loadSlot c acc accTy

/// The ORBIT-CLASS FOLD (Blade.OrbitFold, whose header carries the proof that
/// it is the logical sum regrouped; the C++ lane's twin is
/// CodeGenBinding.genOrbitFoldCpp, same nest, same accumulators, same
/// addition tree). Taken only when the shared recogniser accepts the walk AND
/// the compact operand answers flat reads in this lane (a pool, or a
/// cell-congruent producer); anything else falls back to the logical walk.
and private tryOrbitFold (c: Ctx) (compExpr: IRExpr) (kernelExpr: IRExpr) (initExpr: IRExpr) : Val option =
    let name id = match Map.tryFind id c.ModuleNames with Some n -> n | None -> $"__v{id}"
    match Blade.OrbitFold.recognise id resolveCallable name compExpr kernelExpr initExpr with
    | Blade.OrbitFold.Orbit p ->
        match scalarTyOf (IRTScalar p.AccTy), scalarTyOf (IRTScalar p.ElemTy) with
        // G41 (Codex re-re-review, finding 13): the orbit-class fold
        // evaluates each canonical cell ONCE and weights it, so a source
        // whose evaluation writes (a virtual producer over a writing kernel,
        // or anything the enclosing fold's operand fact flags) would lose
        // evaluations, not just reassociate them. Declined BEFORE anything is
        // emitted; the logical walk keeps the normal forcing / read policy.
        | Some accTy, Some _ when (accTy = ScF64 || accTy = ScI64)
                                  && (c.FoldOperandWrites
                                      || Blade.CodeGenExprSupport.exprMayWriteIn c.BindingValues p.SourceExpr) -> None
        | Some accTy, Some _ when accTy = ScF64 || accTy = ScI64 ->
            let a = emitArr c p.SourceExpr
            (match soleSimplexR a.Groups with
             | Some (r, n, strict) when r = p.Rank && n = p.Extent && strict = p.Strict && hasFlatRead a ->
                 // The walk this replaces reads the source through IRIndex,
                 // whose operand rule (consumeArrOperand) marks a MODULE
                 // binding forced, so a deferred source still prints -- as
                 // the interpreter forces it and the C++ lane materializes
                 // it under its own name. The regrouped fold reads the same
                 // cells, so it keeps that printability fact (gap G49: a
                 // pure deferred map over a symmetric array, read only by a
                 // nested kernel's fold, was left off this lane's print list).
                 (match p.SourceExpr with
                  | IRVar (sid, _) when c.BindingValues.ContainsKey sid -> markForced c sid
                  | _ -> ())
                 Some (emitOrbitFold c p a accTy)
             | _ -> None)
        | _ -> None
    | _ -> None

and private emitOrbitFold (c: Ctx) (p: Blade.OrbitFold.OrbitPlan) (a: ArrVal) (accTy: Sc) : Val =
    let fold = (List.last p.Levels).Fold
    let licensed = foldFmfDecorable c fold
    let n = p.Extent
    let r = p.Rank
    let negBody = Blade.OrbitFold.negatedBody p
    let formLine = Blade.OrbitFold.planForm p
    ln c ($"; {formLine}: the logical sum over the pool of `{p.SourceName}`, regrouped (licensed)")
    // f at a value: bind the read variable to it for the body's emission.
    let evalAt (body: IRExpr) (v: Val) : Val =
        let slot = allocaOf c (llTy v.Ty)
        ln c ($"store {(llTy v.Ty)} {v.Reg}, ptr {slot}")
        let rv = Blade.OrbitFold.readVarId
        let saved = match c.Slots.TryGetValue rv with | true, s -> Some s | _ -> None
        c.Slots.[rv] <- (slot, v.Ty)
        let res = coerce c accTy (emitExpr c body)
        (match saved with
         | Some s -> c.Slots.[rv] <- s
         | None -> c.Slots.Remove rv |> ignore)
        res
    // `x + y` through the fold's own builtin `+`: inside the class loops with
    // the fold licence's flags (`reassoc nsz`, so the vectorizer may lane the
    // class sum), in the combine WITHOUT them (an addition tree LLVM must not
    // re-associate into a product it could then contract).
    let add (inLoop: bool) (x: Val) (y: Val) : Val =
        let saved = c.FoldFmf
        c.FoldFmf <- inLoop && licensed
        try coerce c accTy (applyKernel c fold [ KScalar x; KScalar y ])
        finally c.FoldFmf <- saved
    let zeroAcc = coerce c accTy (emitExpr c (if accTy = ScF64 then IRLit (IRLitFloat 0.0) else IRLit (IRLitInt 0L)))
    let accs = [ for _ in 1 .. p.AccCount -> allocaOf c (llTy accTy) ]
    for s in accs do ln c ($"store {(llTy accTy)} {zeroAcc.Reg}, ptr {s}")
    let bump (k: int) (v: Val) =
        let cur = loadSlot c accs.[k] accTy
        let next = add true cur v
        ln c ($"store {(llTy accTy)} {next.Reg}, ptr {accs.[k]}")
    let weightIndex (mults: int list) =
        let w = Blade.OrbitFold.orbitWeight mults
        p.Weights |> List.findIndex ((=) w)
    let cell (mults: int list) (x: Val) =
        if p.Strict then
            bump 0 (evalAt p.Body x)
            if p.AccCount = 2 then bump 1 (evalAt negBody x)
        else bump (weightIndex mults) (evalAt p.Body x)
    if p.PoolCells > 0L then
        let oo = allocaOf c "i64"
        ln c ($"store i64 0, ptr {oo}")
        let readAt (off: string) = readFlat c a { Reg = off; Ty = ScI64 }
        let incLast (m: int list) = List.take (m.Length - 1) m @ [ List.last m + 1 ]
        let single (mults: int list) =
            let off = (loadSlot c oo ScI64).Reg
            cell mults (readAt off)
            let next = i64Bin c "add" off "1"
            ln c ($"store i64 {next}, ptr {oo}")
        let run (mults: int list) (prev: string) =
            let len = i64Bin c "sub" (string (n - 1L)) prev
            let start = (loadSlot c oo ScI64).Reg
            emitCountedLoopTo c len (fun k -> cell mults (readAt (i64Bin c "add" start k)))
            let next = i64Bin c "add" start len
            ln c ($"store i64 {next}, ptr {oo}")
        let rec gen (t: int) (prev: string) (mults: int list) =
            if t = r then
                if not p.Strict then single (incLast mults)
                run (mults @ [ 1 ]) prev
            else
                if not p.Strict then gen (t + 1) prev (incLast mults)
                let trips = i64Bin c "sub" (string (n - 1L)) prev
                emitCountedLoopTo c trips (fun k ->
                    let coord = i64Bin c "add" (i64Bin c "add" prev "1") k
                    gen (t + 1) coord (mults @ [ 1 ]))
        emitCountedLoop c n (fun i -> gen 2 i [ 1 ])
    orbitCombineLlvm c p a.Elem accTy (accs |> List.map (fun s -> loadSlot c s accTy))

/// The orbit plan's ADDITION-ONLY combine of the class totals `accVals` into
/// `seed + total` (no `reassoc`, so LLVM may not rewrite `rep(S, w)` into a
/// product it could contract). Shared by the orbit-class pass and the compact
/// row-class consumer.
and private orbitCombineLlvm (c: Ctx) (p: Blade.OrbitFold.OrbitPlan) (elem: Sc) (accTy: Sc) (accVals: Val list) : Val =
    let fold = (List.last p.Levels).Fold
    let evalAt (body: IRExpr) (v: Val) : Val =
        let slot = allocaOf c (llTy v.Ty)
        ln c ($"store {(llTy v.Ty)} {v.Reg}, ptr {slot}")
        let rv = Blade.OrbitFold.readVarId
        let saved = match c.Slots.TryGetValue rv with | true, s -> Some s | _ -> None
        c.Slots.[rv] <- (slot, v.Ty)
        let res = coerce c accTy (emitExpr c body)
        (match saved with
         | Some s -> c.Slots.[rv] <- s
         | None -> c.Slots.Remove rv |> ignore)
        res
    let add (x: Val) (y: Val) : Val =
        let saved = c.FoldFmf
        c.FoldFmf <- false
        try coerce c accTy (applyKernel c fold [ KScalar x; KScalar y ])
        finally c.FoldFmf <- saved
    let rec combine (e: Blade.OrbitFold.Combine) : Val =
        match e with
        | Blade.OrbitFold.CAcc k -> accVals.[k]
        | Blade.OrbitFold.CZero ->
            let z = emitExpr c (if elem = ScF64 then IRLit (IRLitFloat 0.0) else IRLit (IRLitInt 0L))
            evalAt p.Body (coerce c elem z)
        | Blade.OrbitFold.CNeg x ->
            let v = combine x
            let d = freshReg c
            if accTy = ScF64 then ln c ($"{d} = fneg double {v.Reg}")
            else ln c ($"{d} = sub i64 0, {v.Reg}")
            { Reg = d; Ty = accTy }
        | Blade.OrbitFold.CAdd (x, y) ->
            let x' = combine x
            let y' = combine y
            add x' y'
        | Blade.OrbitFold.CRep (x, w) ->
            let b = combine x
            let mutable acc = b
            for dbl in Blade.OrbitFold.repSteps w do
                acc <- add acc (if dbl then acc else b)
            acc
    let total = combine p.Total
    let seed = coerce c accTy (emitExpr c p.OuterSeed)
    add seed total

/// THE ORDERED ORBIT SCATTER (Blade.OrbitFold.planStrict, whose header carries
/// the order proof; the C++ lane's twin is blade_orbit_scatter.hpp through
/// CodeGenBinding.genOrbitScatterCpp, same schedule). Taken when the orbit-
/// class fold is not: the shared strict plan admits the walk, the pool answers
/// flat reads in this lane, and every free scalar the cell function, a kernel
/// or a seed reads is bound here. This lane has no threads, so a G35-shaped
/// request still takes the scatter here (the plan translation says so).
and private tryOrbitScatter (c: Ctx) (compExpr: IRExpr) (kernelExpr: IRExpr) (initExpr: IRExpr) : Val option =
    let name id = match Map.tryFind id c.ModuleNames with Some n -> n | None -> $"__v{id}"
    // This lane's OWN record of the decision (the plan's LLVM half: CliCommands
    // puts it in place of the C++ lane's scatter record, so the plan reports
    // what THIS emitter did, including its refusals).
    let record (outcome: Blade.Effects.DecisionOutcome) (evidence: string list) =
        if Blade.Effects.Decisions.active () then
            Blade.Effects.Decisions.record
                { Rule = "fold-form"; Version = 1; Span = c.DeclSpan
                  Subject = (if c.DeclName = "" then "main" else c.DeclName)
                  Outcome = outcome; Evidence = evidence @ [ Blade.OrbitFold.llvmScatterRecordTag ] }
    let decline (why: string) =
        record (Blade.Effects.Declined $"ordered orbit scatter not taken: {why}")
            [ "fold form: ordered orbit scatter not taken -- the logical walk is kept"; why ]
        None
    match Blade.OrbitFold.recogniseStrict id resolveCallable name compExpr kernelExpr initExpr with
    | Blade.OrbitFold.StrictScatter sp ->
        let s = sp.Shape
        match scalarTyOf (IRTScalar s.AccTy), scalarTyOf (IRTScalar s.ElemTy) with
        | Some accTy, Some elemTy when (accTy = ScF64 || accTy = ScI64) && (elemTy = ScF64 || elemTy = ScI64) ->
            if not (sp.FreeIds |> Set.forall (fun v -> c.Slots.ContainsKey v)) then
                decline "a free scalar of the cell function, a fold kernel or a seed is not a scalar slot at the fold (LLVM lane)"
            else
            match c.ArrSlots.TryGetValue s.SourceId with
            // A STORED pool only (v1): a flat-addressable PRODUCER (AVirtFlat)
            // would be EXECUTED by the scatter's reads -- a map planStrict never
            // audited, in the grouped order -- and would skip the ordinary
            // operand rule's forcing; it keeps the walk.
            | true, a ->
                (match a.Src, soleSimplexR a.Groups with
                 | APool _, Some (r, n, strict) when r = s.Rank && n = s.Extent && not strict ->
                     record Blade.Effects.Applied
                         [ "fold form: " + Blade.OrbitFold.scatterForm sp; Blade.OrbitFold.scatterEvidence sp ]
                     Some (emitOrbitScatter c sp a accTy)
                 | APool _, _ -> decline "the pool's storage groups are not one symmetric simplex of the walk's rank and extent (LLVM lane)"
                 | _ -> decline "the compact array is an unforced computation, not a stored pool (LLVM lane: its producer would run inside the scatter)")
            | _ -> decline "the compact array is not a materialized array in scope (LLVM lane)"
        | _ -> decline "the accumulator or element type is not Float64 / Int64 (LLVM lane)"
    | _ -> None

and private emitOrbitScatter (c: Ctx) (sp: Blade.OrbitFold.StrictPlan) (a: ArrVal) (accTy: Sc) : Val =
    let s = sp.Shape
    let n = s.Extent
    let g = sp.RowGroup
    let blk = sp.ColBlock
    let ety = poolTy accTy
    ln c ($"; {(Blade.OrbitFold.scatterForm sp)} (strict: no licence consumed)")
    // Every arithmetic instruction below is born WITHOUT `reassoc nsz`: no
    // chain is reassociated (lanes are distinct chains); `contract` follows
    // the build as everywhere (`fmfFor`).
    let savedFmf = c.FoldFmf
    c.FoldFmf <- false
    let evalF (body: IRExpr) (v: Val) : Val =
        let slot = allocaOf c (llTy v.Ty)
        ln c ($"store {(llTy v.Ty)} {v.Reg}, ptr {slot}")
        let rv = Blade.OrbitFold.readVarId
        let saved = match c.Slots.TryGetValue rv with | true, sl -> Some sl | _ -> None
        c.Slots.[rv] <- (slot, v.Ty)
        let res = coerce c accTy (emitExpr c body)
        (match saved with
         | Some sl -> c.Slots.[rv] <- sl
         | None -> c.Slots.Remove rv |> ignore)
        res
    let kern (k: int) (x: Val) (y: Val) : Val =
        coerce c accTy (applyKernel c s.Levels.[k].Fold [ KScalar x; KScalar y ])
    let gep (basePtr: string) (off: string) = gepCell c accTy basePtr off
    let ld (basePtr: string) (off: string) : Val = loadCell c accTy (gep basePtr off)
    let st (basePtr: string) (off: string) (v: Val) = storeCell c accTy (gep basePtr off) v
    let alloc (cells: string) : string =
        needShim c "blade_alloc_cells"
        let p = freshReg c
        ln c (renderCall { Dest = Some p; RetTy = ScStr; Callee = "@blade_alloc_cells"
                           Args = [ ScI64, cells; ScI64, string (poolElemBytes accTy) ] })
        p
    let free (p: string) =
        needShim c "blade_free"
        c.AnyFrees <- true
        ln c ($"call void @blade_free(ptr {p})")
    let ifThen (cond: string) (body: unit -> unit) =
        let lT = freshLbl c "sc.then"
        let lE = freshLbl c "sc.end"
        ln c (renderBr { Cond = cond; IfTrue = lT; IfFalse = lE })
        lbl c lT
        body ()
        ln c (sprintf "br label %%%s" lE)
        lbl c lE
    let icmp (pred: string) (lhs: string) (rhs: string) =
        let d = freshReg c
        ln c (renderCmp { Dest = d; Kind = "icmp"; Pred = pred; Ty = ScI64; Lhs = lhs; Rhs = rhs })
        d
    let imin (x: string) (y: string) =
        let lt = icmp "slt" x y
        let d = freshReg c
        ln c ($"{d} = select i1 {lt}, i64 {x}, i64 {y}")
        d
    // first pool cell of row r of a packed SymIdx<2, m> triangle:
    // r*m - r*(r-1)/2 (r*(r-1) is even and >= 0)
    let triRow (m: string) (r: string) =
        let t1 = i64Bin c "mul" r m
        let t3 = i64Bin c "mul" r (i64Bin c "sub" r "1")
        i64Bin c "sub" t1 (i64Bin c "lshr" t3 "1")
    let zbuf = allocaOf c $"[{g * blk} x {ety}]"
    let vaddAt (k: int) = sp.LevelAdd.[k] && accTy = ScF64 && g % 4 = 0 && Blade.CodeGenExprSupport.scatterVectorRows ()
    let fill (p: string) (cells: string) (v: Val) =
        emitCountedLoopTo c cells (fun i -> st p i v)
    // THE GROUP SCATTER over one packed triangle of extent `m` (row r at pool
    // offset base + triRow(m, r)) into chains R[0 .. m); `third`: (H, hRow0,
    // hn) -- every term of a row r >= 1 also feeds H's chain (hRow0 + r,
    // hRow0 + c). Same schedule as blade_scatter::tri.
    let tri (read: string -> Val) (f: Val -> Val) (kk: Val -> Val -> Val) (vadd: bool) (m: string) (rPtr: string)
            (third: (string * string * string) option) =
        let groups = i64Bin c "udiv" m (string g)
        emitCountedLoopTo c groups (fun gi ->
            let i0 = i64Bin c "mul" gi (string g)
            let rs = [| for q in 0 .. g - 1 -> triRow m (i64Add c i0 (string q)) |]
            let hrs =
                match third with
                | Some (_, h0, hn) -> [| for q in 0 .. g - 1 -> triRow hn (i64Add c h0 (i64Add c i0 (string q))) |]
                | None -> [||]
            // `i0 + q >= 1` holds for every q >= 1; for q = 0 it is a test
            let thirdGuard (q: int) (body: unit -> unit) =
                match third with
                | None -> ()
                | Some _ when q >= 1 -> body ()
                | Some _ -> ifThen (icmp "sge" i0 "1") body
            let hPtr = match third with Some (h, _, _) -> h | None -> ""
            // (1) the diagonal block, block-lex
            for q in 0 .. g - 1 do
                for q2 in q .. g - 1 do
                    let z = f (read (i64Add c rs.[q] (string (q2 - q))))
                    let ra = i64Add c i0 (string q)
                    st rPtr ra (kk (ld rPtr ra) z)
                    if q2 <> q then
                        let rb = i64Add c i0 (string q2)
                        st rPtr rb (kk (ld rPtr rb) z)
                    thirdGuard q (fun () ->
                        let off = i64Add c hrs.[q] (string (q2 - q))
                        st hPtr off (kk (ld hPtr off) z))
            // the group's row chains, held across the column blocks: G scalar
            // slots, or (VADD: the builtin `+` on double, G % 4 = 0) G / 4
            // `<4 x double>` slots whose lane m is chain R[i0 + 4v + m] -- one
            // vertical `fadd` per column per 4 chains, no `reassoc`, no
            // reduction (the C++ lane's v4d row chains)
            let nv = g / 4
            let rc = if vadd then [||] else [| for q in 0 .. g - 1 -> allocaOf c ety |]
            let rcv = if vadd then [| for _ in 0 .. nv - 1 -> allocaOf c "<4 x double>" |] else [||]
            if vadd then
                for v in 0 .. nv - 1 do
                    let x = freshReg c
                    ln c ($"{x} = load <4 x double>, ptr {(gep rPtr (i64Add c i0 (string (4 * v))))}, align 8")
                    ln c ($"store <4 x double> {x}, ptr {rcv.[v]}")
            else
                for q in 0 .. g - 1 do
                    let v = ld rPtr (i64Add c i0 (string q))
                    ln c ($"store {ety} {v.Reg}, ptr {rc.[q]}")
            let c0 = i64Add c i0 (string g)
            let ncols = i64Bin c "sub" m c0
            let nblocks = i64Bin c "udiv" (i64Bin c "add" ncols (string (blk - 1))) (string blk)
            emitCountedLoopTo c nblocks (fun bi ->
                let jb = i64Add c c0 (i64Bin c "mul" bi (string blk))
                let len = imin (string blk) (i64Bin c "sub" m jb)
                // (2a) the term map: G contiguous runs of the pool
                for q in 0 .. g - 1 do
                    let rowOff = i64Add c rs.[q] (i64Bin c "sub" jb (i64Add c i0 (string q)))
                    emitCountedLoopTo c len (fun jj ->
                        let z = f (read (i64Add c rowOff jj))
                        st zbuf (i64Add c (string (q * blk)) jj) z)
                // (2b) the column chains (lane jj = distinct chain R[jb + jj])
                emitCountedLoopTo c len (fun jj ->
                    let col = i64Add c jb jj
                    let mutable acc = ld rPtr col
                    for q in 0 .. g - 1 do
                        acc <- kk acc (ld zbuf (i64Add c (string (q * blk)) jj))
                    st rPtr col acc)
                // (2c) the row chains: one application per chain per column
                if vadd then
                    let flags = fmfFor c ScF64
                    emitCountedLoopTo c len (fun jj ->
                        for v in 0 .. nv - 1 do
                            let acc = freshReg c
                            ln c ($"{acc} = load <4 x double>, ptr {rcv.[v]}")
                            let mutable vec = "poison"
                            for m in 0 .. 3 do
                                let z = ld zbuf (i64Add c (string ((4 * v + m) * blk)) jj)
                                let nx = freshReg c
                                ln c ($"{nx} = insertelement <4 x double> {vec}, double {z.Reg}, i32 {m}")
                                vec <- nx
                            let sum = freshReg c
                            ln c ($"{sum} = fadd{flags} <4 x double> {acc}, {vec}")
                            ln c ($"store <4 x double> {sum}, ptr {rcv.[v]}"))
                else
                    emitCountedLoopTo c len (fun jj ->
                        for q in 0 .. g - 1 do
                            let cur = loadSlot c rc.[q] accTy
                            let next = kk cur (ld zbuf (i64Add c (string (q * blk)) jj))
                            ln c ($"store {ety} {next.Reg}, ptr {rc.[q]}"))
                // (2d) the third event, from the same term block
                for q in 0 .. g - 1 do
                    thirdGuard q (fun () ->
                        let hOff = i64Add c hrs.[q] (i64Bin c "sub" jb (i64Add c i0 (string q)))
                        emitCountedLoopTo c len (fun jj ->
                            let off = i64Add c hOff jj
                            st hPtr off (kk (ld hPtr off) (ld zbuf (i64Add c (string (q * blk)) jj))))))
            if vadd then
                for v in 0 .. nv - 1 do
                    let x = freshReg c
                    ln c ($"{x} = load <4 x double>, ptr {rcv.[v]}")
                    ln c ($"store <4 x double> {x}, ptr {(gep rPtr (i64Add c i0 (string (4 * v))))}, align 8")
            else
                for q in 0 .. g - 1 do
                    st rPtr (i64Add c i0 (string q)) (loadSlot c rc.[q] accTy))
        // the tail rows, one at a time: the diagonal cell, then the columns
        let done0 = i64Bin c "mul" groups (string g)
        emitCountedLoopTo c (i64Bin c "sub" m done0) (fun t ->
            let i0 = i64Add c done0 t
            let row = triRow m i0
            let hrow =
                match third with
                | Some (_, h0, hn) -> triRow hn (i64Add c h0 i0)
                | None -> ""
            let thirdAt (jj: string) (z: Val) =
                match third with
                | Some (h, _, _) ->
                    ifThen (icmp "sge" i0 "1") (fun () ->
                        let off = i64Add c hrow jj
                        st h off (kk (ld h off) z))
                | None -> ()
            let zd = f (read row)
            st rPtr i0 (kk (ld rPtr i0) zd)
            thirdAt "0" zd
            emitCountedLoopTo c (i64Bin c "sub" (i64Bin c "sub" m i0) "1") (fun t2 ->
                let jj = i64Add c t2 "1"
                let z = f (read (i64Add c row jj))
                st rPtr i0 (kk (ld rPtr i0) z)
                let j = i64Add c i0 jj
                st rPtr j (kk (ld rPtr j) z)
                thirdAt jj z))
    let poolRead (baseOff: string) (off: string) : Val = readFlat c a { Reg = i64Add c baseOff off; Ty = ScI64 }
    let fCell (v: Val) = evalF s.Body v
    let seedVal (k: int) = coerce c accTy (emitExpr c s.Levels.[k].Seed)
    // the outer seed first (the walk's order), then the hoisted inner seeds
    let outSeed = seedVal 0
    let outSlot = allocaOf c ety
    ln c ($"store {ety} {outSeed.Reg}, ptr {outSlot}")
    let rows (hPtr: string option) (midSeed: Val) (midK: int) =
        // the rank-2 scatter's chains over `n` rows, then the outer chain
        let rPtr = alloc (string n)
        fill rPtr (string n) midSeed
        (match hPtr with
         | None -> tri (poolRead "0") fCell (kern 1) (vaddAt 1) (string n) rPtr None
         | Some h -> tri (fun off -> ld h off) id (kern midK) (vaddAt midK) (string n) rPtr None)
        emitCountedLoop c n (fun i ->
            let cur = loadSlot c outSlot accTy
            let next = kern 0 cur (ld rPtr i)
            ln c ($"store {ety} {next.Reg}, ptr {outSlot}"))
        free rPtr
    if s.Rank = 2 then
        rows None (seedVal 1) 1
    else
        let leafSeed = seedVal 2
        let midSeed = seedVal 1
        let hc = n * (n + 1L) / 2L
        let h = alloc (string hc)
        fill h (string hc) leafSeed
        let sliceSlot = allocaOf c "i64"
        ln c ($"store i64 0, ptr {sliceSlot}")
        emitCountedLoop c n (fun i ->
            let m = i64Bin c "sub" (string n) i
            let sliceStart = (loadSlot c sliceSlot ScI64).Reg
            let rPtr = gep h (triRow (string n) i)
            tri (poolRead sliceStart) fCell (kern 2) (vaddAt 2) m rPtr (Some (h, i, string n))
            let cells = i64Bin c "lshr" (i64Bin c "mul" m (i64Bin c "add" m "1")) "1"
            let next = i64Bin c "add" sliceStart cells
            ln c ($"store i64 {next}, ptr {sliceSlot}"))
        rows (Some h) midSeed 1
        free h
    c.FoldFmf <- savedFmf
    loadSlot c outSlot accTy

/// THE ROW ORBIT ACTION in this lane (round 3, D-P2 / gap G73; Blade.OrbitFold's
/// action header carries the role-term rule and the order proof; the C++ twin
/// is blade_orbit_scatter.hpp action2 / tri_action through
/// CodeGenBinding.tryGenOrbitActionCpp, same schedule). A `|> compute` row
/// action over a STORED SymIdx<2, n> pool whose weights are stored dense
/// rank-1 pools and whose free scalars are slots here, admitted by the shared
/// admission: its own pool is allocated and filled by one pass over the
/// canonical cells. This lane has no thread runtime: a request on the map or
/// the fold is serial here as everywhere in this lane (recorded). False --
/// nothing emitted -- when it is not taken (a recognised action records why).
and private tryBindOrbitAction (c: Ctx) (id: IRId) (name: string) (declTy: IRType) (info: ApplyInfo) : bool =
    let srcName i = match Map.tryFind i c.ModuleNames with Some n -> n | None -> $"__v{i}"
    let record (outcome: Blade.Effects.DecisionOutcome) (evidence: string list) =
        if Blade.Effects.Decisions.active () then
            Blade.Effects.Decisions.record
                { Rule = "fold-form"; Version = 1; Span = c.DeclSpan
                  Subject = (if name <> "" then name elif c.DeclName = "" then "main" else c.DeclName)
                  Outcome = outcome; Evidence = evidence @ [ Blade.OrbitFold.llvmScatterRecordTag ] }
    let decline (why: string) =
        record (Blade.OrbitFold.actionDeclineOutcome why) [ Blade.OrbitFold.actionDeclinedForm; why ]
        false
    match Blade.OrbitFold.recogniseAction (fun e -> e) resolveCallable srcName info with
    | Blade.OrbitFold.ActionNotAction -> false
    | Blade.OrbitFold.ActionDeclined why -> decline why
    | Blade.OrbitFold.ActionTaken p ->
    let s = p.Shape
    let n = s.Extent
    let outOk =
        match stripUnits declTy with
        | ArrayElem at when not at.IsVirtual && Blade.CodeGenExprSupport.isFreeableDenseArrayType at ->
            (match at.IndexTypes, Blade.IRTotality.scalarElem at.ElemType with
             | [ ix ], Some et -> ix.Rank = 1 && ix.Symmetry = SymNone && Blade.IRPrint.tryEvalIntIR ix.Extent = Some n && et = s.AccTy
             | _ -> false)
        | _ -> false
    match scalarTyOf (IRTScalar s.AccTy), scalarTyOf (IRTScalar s.ElemTy) with
    | Some accTy, Some elemTy when (accTy = ScF64 || accTy = ScI64) && (elemTy = ScF64 || elemTy = ScI64) ->
        if not outOk then decline "the output is not a dense rank-1 array of the fold's accumulator type over the pool's index"
        elif (Blade.Optimize.HotRowTable.tryProducer id).IsSome || (Blade.Optimize.CompactRowTable.tryProducer id).IsSome
             || (Blade.Optimize.EpilogueLegTable.tryProducer id).IsSome then
            decline "another schedule (a hot-row fold, a cell epilogue, compact row classes or producer-input legs) owns this producer"
        elif s.Weights |> List.exists (fun w -> match scalarTyOf (IRTScalar w.ElemTy) with Some t -> t <> ScF64 && t <> ScI64 | None -> true) then
            decline "a weight's element type is not Float64 / Int64 (LLVM lane)"
        elif not (p.FreeIds |> Set.forall (fun v -> c.Slots.ContainsKey v)) then
            decline "a free scalar of the cell term, the fold kernel or the seed is not a scalar slot at the action (LLVM lane)"
        else
        match c.ArrSlots.TryGetValue s.SourceId with
        | true, pool ->
            (match pool.Src, soleSimplexR pool.Groups with
             | APool _, Some (2, pn, false) when pn = n ->
                 let weights =
                     s.Weights |> List.map (fun w ->
                         match c.ArrSlots.TryGetValue w.ArrayId with
                         | true, ({ Src = APool _ } as wa) when wa.Groups = [ GDense n ] -> Some (w, wa)
                         | _ -> None)
                 if weights |> List.exists Option.isNone then
                     decline "a weight is not a stored dense rank-1 pool in scope (LLVM lane: a deferred weight would run its producer inside the action)"
                 else
                 match Blade.OrbitFold.orbitActionAdmissible c.BindingValues id p with
                 | Error why -> decline why
                 | Ok matrix ->
                 let reqs = Blade.OrbitFold.actionThreadRequests p
                 let note =
                     if reqs.IsEmpty then []
                     else [ $"""a thread request on {String.concat ", " reqs}: this lane has no thread runtime -- the action is serial here as every fold in this lane""" ]
                 record Blade.Effects.Applied
                     ([ "fold form: " + Blade.OrbitFold.actionForm p; Blade.OrbitFold.actionEvidence p ] @ matrix @ note)
                 let out = allocPool c accTy (string n)
                 emitOrbitAction c p pool (weights |> List.choose (fun w -> w)) out accTy
                 bindArray c id name { Elem = accTy; Groups = [ GDense n ]; Src = APool out; RowOpBytes = 0L }
                 true
             | APool _, _ -> decline "the pool's storage groups are not one SymIdx<2, n> simplex of the action's extent (LLVM lane)"
             | _ -> decline "the compact array is an unforced computation, not a stored pool (LLVM lane: its producer would run inside the action)")
        | _ -> decline "the compact array is not a materialized array in scope (LLVM lane)"
    | _ -> decline "the accumulator or element type is not Float64 / Int64 (LLVM lane)"

/// The action's pass (blade_scatter::tri_action's schedule): every chain of
/// the output pool `out` seeded once, then the G-row groups -- the diagonal
/// block block-lex, then per column block the role terms of each canonical
/// cell into Zr (the row chain's, own = row) and Zc (the column chain's, own =
/// column), the column chains in row order, the row chains one application
/// per column -- and the tail rows one at a time. Every instruction is born
/// WITHOUT `reassoc nsz` (lanes are distinct chains); `contract` follows the
/// build (`fmfFor`).
and private emitOrbitAction (c: Ctx) (p: Blade.OrbitFold.ActionPlan) (pool: ArrVal)
                            (weights: (Blade.OrbitFold.ActionWeight * ArrVal) list) (out: string) (accTy: Sc) : unit =
    let s = p.Shape
    let n = s.Extent
    let g = p.RowGroup
    let blk = p.ColBlock
    let ety = poolTy accTy
    ln c ($"; {(Blade.OrbitFold.actionForm p)} (strict: no licence consumed)")
    let savedFmf = c.FoldFmf
    c.FoldFmf <- false
    // f(x, own, partner): the term with the cell value and each weight's own /
    // partner value bound to the plan's sentinels
    let evalF (x: Val) (own: string) (partner: string) : Val =
        let binds = System.Collections.Generic.List<IRId * (string * Sc) option>()
        let bindSlot (vid: IRId) (v: Val) =
            let slot = allocaOf c (llTy v.Ty)
            ln c ($"store {(llTy v.Ty)} {v.Reg}, ptr {slot}")
            let saved = match c.Slots.TryGetValue vid with | true, sl -> Some sl | _ -> None
            c.Slots.[vid] <- (slot, v.Ty)
            binds.Add((vid, saved))
        bindSlot Blade.OrbitFold.readVarId x
        for (w, wa) in weights do
            if w.Own then bindSlot (Blade.OrbitFold.actionOwnId w.Index) (readFlat c wa { Reg = own; Ty = ScI64 })
            if w.Partner then bindSlot (Blade.OrbitFold.actionPartnerId w.Index) (readFlat c wa { Reg = partner; Ty = ScI64 })
        let res = coerce c accTy (emitExpr c s.Body)
        for (vid, saved) in binds do
            match saved with
            | Some sl -> c.Slots.[vid] <- sl
            | None -> c.Slots.Remove vid |> ignore
        res
    let kk (x: Val) (y: Val) : Val = coerce c accTy (applyKernel c s.Level.Fold [ KScalar x; KScalar y ])
    let gep (basePtr: string) (off: string) = gepCell c accTy basePtr off
    let ld (basePtr: string) (off: string) : Val = loadCell c accTy (gep basePtr off)
    let st (basePtr: string) (off: string) (v: Val) = storeCell c accTy (gep basePtr off) v
    let icmp (pred: string) (lhs: string) (rhs: string) =
        let d = freshReg c
        ln c (renderCmp { Dest = d; Kind = "icmp"; Pred = pred; Ty = ScI64; Lhs = lhs; Rhs = rhs })
        d
    let imin (x: string) (y: string) =
        let lt = icmp "slt" x y
        let d = freshReg c
        ln c ($"{d} = select i1 {lt}, i64 {x}, i64 {y}")
        d
    let triRow (m: string) (r: string) =
        let t1 = i64Bin c "mul" r m
        let t3 = i64Bin c "mul" r (i64Bin c "sub" r "1")
        i64Bin c "sub" t1 (i64Bin c "lshr" t3 "1")
    let read (off: string) : Val = readFlat c pool { Reg = off; Ty = ScI64 }
    let zr = allocaOf c $"[{g * blk} x {ety}]"
    let zc = allocaOf c $"[{g * blk} x {ety}]"
    let vadd = p.LevelAdd && accTy = ScF64 && g % 4 = 0 && Blade.CodeGenExprSupport.scatterVectorRows ()
    let m = string n
    // every chain seeded once, from the seed evaluated once (total,
    // coordinate-free)
    let seedVal = coerce c accTy (emitExpr c s.Level.Seed)
    emitCountedLoopTo c m (fun i -> st out i seedVal)
    let groups = i64Bin c "udiv" m (string g)
    emitCountedLoopTo c groups (fun gi ->
        let i0 = i64Bin c "mul" gi (string g)
        let rs = [| for q in 0 .. g - 1 -> triRow m (i64Add c i0 (string q)) |]
        // (1) the diagonal block, block-lex: the row role to R[i0 + q], the
        // column role to R[i0 + q2]
        for q in 0 .. g - 1 do
            for q2 in q .. g - 1 do
                let x = read (i64Add c rs.[q] (string (q2 - q)))
                let ra = i64Add c i0 (string q)
                let rb = i64Add c i0 (string q2)
                st out ra (kk (ld out ra) (evalF x ra rb))
                if q2 <> q then st out rb (kk (ld out rb) (evalF x rb ra))
        let nv = g / 4
        let rc = if vadd then [||] else [| for _ in 0 .. g - 1 -> allocaOf c ety |]
        let rcv = if vadd then [| for _ in 0 .. nv - 1 -> allocaOf c "<4 x double>" |] else [||]
        if vadd then
            for v in 0 .. nv - 1 do
                let x = freshReg c
                ln c ($"{x} = load <4 x double>, ptr {(gep out (i64Add c i0 (string (4 * v))))}, align 8")
                ln c ($"store <4 x double> {x}, ptr {rcv.[v]}")
        else
            for q in 0 .. g - 1 do
                let v = ld out (i64Add c i0 (string q))
                ln c ($"store {ety} {v.Reg}, ptr {rc.[q]}")
        let c0 = i64Add c i0 (string g)
        let ncols = i64Bin c "sub" m c0
        let nblocks = i64Bin c "udiv" (i64Bin c "add" ncols (string (blk - 1))) (string blk)
        emitCountedLoopTo c nblocks (fun bi ->
            let jb = i64Add c c0 (i64Bin c "mul" bi (string blk))
            let len = imin (string blk) (i64Bin c "sub" m jb)
            // (2a) the term map: each canonical cell read once, its two role
            // terms formed (Zr: own = the row; Zc: own = the column)
            for q in 0 .. g - 1 do
                let own = i64Add c i0 (string q)
                let rowOff = i64Add c rs.[q] (i64Bin c "sub" jb own)
                emitCountedLoopTo c len (fun jj ->
                    let x = read (i64Add c rowOff jj)
                    let col = i64Add c jb jj
                    let slot = i64Add c (string (q * blk)) jj
                    st zr slot (evalF x own col)
                    st zc slot (evalF x col own))
            // (2b) the column chains (lane jj = distinct chain R[jb + jj]),
            // their G column-role terms in row order
            emitCountedLoopTo c len (fun jj ->
                let col = i64Add c jb jj
                let mutable acc = ld out col
                for q in 0 .. g - 1 do
                    acc <- kk acc (ld zc (i64Add c (string (q * blk)) jj))
                st out col acc)
            // (2c) the row chains: one application per chain per column
            if vadd then
                let flags = fmfFor c ScF64
                emitCountedLoopTo c len (fun jj ->
                    for v in 0 .. nv - 1 do
                        let acc = freshReg c
                        ln c ($"{acc} = load <4 x double>, ptr {rcv.[v]}")
                        let mutable vec = "poison"
                        for mm in 0 .. 3 do
                            let z = ld zr (i64Add c (string ((4 * v + mm) * blk)) jj)
                            let nx = freshReg c
                            ln c ($"{nx} = insertelement <4 x double> {vec}, double {z.Reg}, i32 {mm}")
                            vec <- nx
                        let sum = freshReg c
                        ln c ($"{sum} = fadd{flags} <4 x double> {acc}, {vec}")
                        ln c ($"store <4 x double> {sum}, ptr {rcv.[v]}"))
            else
                emitCountedLoopTo c len (fun jj ->
                    for q in 0 .. g - 1 do
                        let cur = loadSlot c rc.[q] accTy
                        let next = kk cur (ld zr (i64Add c (string (q * blk)) jj))
                        ln c ($"store {ety} {next.Reg}, ptr {rc.[q]}")))
        if vadd then
            for v in 0 .. nv - 1 do
                let x = freshReg c
                ln c ($"{x} = load <4 x double>, ptr {rcv.[v]}")
                ln c ($"store <4 x double> {x}, ptr {(gep out (i64Add c i0 (string (4 * v))))}, align 8")
        else
            for q in 0 .. g - 1 do
                st out (i64Add c i0 (string q)) (loadSlot c rc.[q] accTy))
    // the tail rows, one at a time: the diagonal cell, then the columns
    let done0 = i64Bin c "mul" groups (string g)
    emitCountedLoopTo c (i64Bin c "sub" m done0) (fun t ->
        let i0 = i64Add c done0 t
        let row = triRow m i0
        st out i0 (kk (ld out i0) (evalF (read row) i0 i0))
        emitCountedLoopTo c (i64Bin c "sub" (i64Bin c "sub" m i0) "1") (fun t2 ->
            let jj = i64Add c t2 "1"
            let j = i64Add c i0 jj
            let x = read (i64Add c row jj)
            st out i0 (kk (ld out i0) (evalF x i0 j))
            st out j (kk (ld out j) (evalF x j i0))))
    c.FoldFmf <- savedFmf

/// `reduce(<deferred computation>, op, init)` -- the FUSED terminal. One nest,
/// no materialized array, and one accumulator PER FUSION LEAF: an `<&!>` tree
/// answers a flat pack of scalars, which is how "several statistics in one
/// pass" costs one traversal.
and private emitReduceCompute (c: Ctx) (compExpr: IRExpr) (kernelExpr: IRExpr) (initExpr: IRExpr) : Val list =
    // G41: a fold over a deferred computation whose kernels WRITE keeps its
    // serial chain in this lane too (no fold FMF), as the C++ lane does.
    let saved = c.FoldOperandWrites
    c.FoldOperandWrites <- saved || Blade.CodeGenExprSupport.exprMayWriteIn c.BindingValues compExpr
    try emitReduceComputeCore c compExpr kernelExpr initExpr
    finally c.FoldOperandWrites <- saved

and private emitReduceComputeCore (c: Ctx) (compExpr: IRExpr) (kernelExpr: IRExpr) (initExpr: IRExpr) : Val list =
    match tryOrbitFold c compExpr kernelExpr initExpr with
    | Some v -> [ v ]
    | None ->
    match tryOrbitScatter c compExpr kernelExpr initExpr with
    | Some v -> [ v ]
    | None ->
    // THE ROW ORBIT ACTION's walk form (round 3, D-P2): a walk folding weighted
    // rows is recognised and declined in v1 (this lane's own record).
    (if Blade.Effects.Decisions.active () then
        let name id = match Map.tryFind id c.ModuleNames with Some n -> n | None -> $"__v{id}"
        match Blade.OrbitFold.recogniseActionWalk (fun e -> e) resolveCallable name compExpr kernelExpr initExpr with
        | Some why ->
            Blade.Effects.Decisions.record
                { Rule = "fold-form"; Version = 1; Span = c.DeclSpan
                  Subject = (if c.DeclName = "" then "main" else c.DeclName)
                  Outcome = Blade.OrbitFold.actionDeclineOutcome why
                  Evidence = [ Blade.OrbitFold.actionDeclinedForm; why; Blade.OrbitFold.llvmScatterRecordTag ] }
        | None -> ())
    let rec leaves e =
        match e with
        | IRFusion (l, r) -> leaves l @ leaves r
        | IRVar (id, _) when c.ArrSlots.ContainsKey id -> [ e ]
        | other -> [ other ]
    let legs = leaves compExpr
    let kernels =
        match kernelExpr with
        | IRTuple ks -> ks
        | k -> List.replicate legs.Length k
    let seeds =
        match initExpr with
        | IRTuple ss -> ss
        | s -> List.replicate legs.Length s
    if kernels.Length <> legs.Length || seeds.Length <> legs.Length then
        refuse "a fused reduction whose kernel/seed pack does not match its leaf count"
    let arrs = legs |> List.map (emitArr c)
    let domainShape =
        match (List.head arrs).Groups with
        | [ GDomain plan ] -> Some plan
        | _ -> None
    let shape = (List.head arrs).Groups
    for a in arrs do
        if a.Groups <> shape then refuse "a fused reduction over legs of different shapes"
    let plans =
        List.map3 (fun (a: ArrVal) k s ->
            let cl =
                match resolveCallable k with
                | Some cl when cl.Params.Length = 2 -> cl
                | Some cl -> refuse ($"a fold kernel '{cl.Name}' of {cl.Params.Length} parameters")
                | None -> refuse "a fused reduction whose kernel does not resolve to a callable"
            let ty =
                match scalarTyOf cl.RetType with
                | Some t when t <> ScVoid -> t
                | _ -> a.Elem
            let slot = allocaOf c (llTy ty)
            let seed = coerce c ty (emitExpr c s)
            ln c ($"store {(llTy ty)} {seed.Reg}, ptr {slot}")
            // PER LEG: one leg of a join may be a `comm`-declared kernel and
            // its neighbour an arbitrary one, and they share a nest without
            // sharing a license.
            (a, cl, ty, slot, foldFmfDecorable c cl)) arrs kernels seeds
    // A COMPACT domain goes through the blocked-simplex fold, one leg at a
    // time: a fused join shares ONE nest by construction, and the bricked nest
    // is a different nest per leg's licence, so mixing them would either
    // license the unlicensed leg or serialize the licensed one. One leg is the
    // shape every reachable program has anyway.
    if shape |> List.exists grpCompact then
        match plans with
        | [ (a, cl, ty, _, _) ] -> [ emitCompactFold c a cl ty (Some (List.head seeds)) ]
        | _ -> refuse "a fused reduction join over compact (symmetric/antisymmetric) storage"
    else
    // THE ORDER: the C++ lane's decision (CodeGenExprSupport.domainFoldOrder).
    // ONE leg only, as there: a fused join of several folds keeps the lex nest
    // (lade plan says so), so the lanes never walk different orders.
    let diagonal =
        match domainShape, plans with
        | Some plan, [ (a, cl, _, _, licensed) ] when licensed && kernels.Length = 1 ->
            (match a.Src with
             | AVirt produce ->
                 let body = match c.DomainMapBodies.TryGetValue (box produce) with | true, b -> Some b | _ -> None
                 (match Blade.CodeGenExprSupport.domainFoldOrder (Some cl) body plan with
                  | Blade.CodeGenExprSupport.DomainDiagonalOrder dg -> Some dg
                  | Blade.CodeGenExprSupport.DomainLexOrder _ -> None)
             | _ -> None)
        | _ -> None
    match domainShape, diagonal with
    | Some _, Some dg ->
        emitDomainDiagonalNest c dg (fun idxs ->
            for (a, cl, ty, slot, licensed) in plans do
                let cur = loadSlot c slot ty
                let cell = readCell c a idxs
                let next =
                    withFoldFmf c licensed (fun () ->
                        coerce c ty (applyKernel c cl [ KScalar cur; KScalar cell ]))
                ln c ($"store {(llTy ty)} {next.Reg}, ptr {slot}"))
        plans |> List.map (fun (_, _, ty, slot, _) -> loadSlot c slot ty)
    | _ ->
    // A CLOSED-FORM DOMAIN: the fold streams the affine nest -- no pool, no
    // key table -- accumulating in lex order exactly as the serial C++ nest
    // and the interpreter do.
    match domainShape with
    | Some plan ->
        emitDomainNest c plan (fun idxs _ ->
            for (a, cl, ty, slot, licensed) in plans do
                let cur = loadSlot c slot ty
                let cell = readCell c a idxs
                let next =
                    withFoldFmf c licensed (fun () ->
                        coerce c ty (applyKernel c cl [ KScalar cur; KScalar cell ]))
                ln c ($"store {(llTy ty)} {next.Reg}, ptr {slot}"))
        plans |> List.map (fun (_, _, ty, slot, _) -> loadSlot c slot ty)
    | None ->
    emitNest c (axisExtents shape) (fun idxs ->
        for (a, cl, ty, slot, licensed) in plans do
            let cur = loadSlot c slot ty
            let cell = readCell c a idxs
            let next =
                withFoldFmf c licensed (fun () ->
                    coerce c ty (applyKernel c cl [ KScalar cur; KScalar cell ]))
            ln c ($"store {(llTy ty)} {next.Reg}, ptr {slot}"))
    plans |> List.map (fun (_, _, ty, slot, _) -> loadSlot c slot ty)

/// Flatten a pack expression into its components. `<&>` (independent) and
/// `<&!>` (fused) both answer a pack of computations, and both associate --
/// `a <&> b <&> c` is three legs, not a nest -- so the walk flattens.
and private emitTupleParts (c: Ctx) (e: IRExpr) : ValKind list =
    let onSpine = spineStatement c e
    match e with
    // A tuple ALL of whose members are deferred computations is a deferred
    // tuple (the C++ lane's isWhollyDeferredTuple): its legs stay producers.
    // Any other tuple is a VALUE (formalism 10.2, "Deferred members of a tuple
    // value", gaps G58 / G61): each member that is still a producer -- a bare
    // deferred map, a named deferred tuple's legs -- is MATERIALIZED right
    // after it is emitted, before the next member, so its evaluation (and its
    // first failure) happens at the construction, left to right, as in the
    // C++ lane and the interpreter. The pack used to keep the producer and run
    // it only where the tuple was printed or projected.
    | IRTuple elems ->
        let isDeferredElem (x: IRExpr) =
            match x with
            | IRApplyCombinator _ | IRComposeApply _ | IRParallel _ | IRFusion _ | IRFunctorMap _ -> true
            | IRVar (id, _) ->
                (match c.ArrSlots.TryGetValue id with
                 | true, { Src = (AVirt _ | AVirtFlat _) } -> true
                 | _ ->
                     match c.TupleSlots.TryGetValue id with
                     | true, parts ->
                         not parts.IsEmpty
                         && parts |> List.forall (function
                             | VArray { Src = (AVirt _ | AVirtFlat _) } -> true
                             | _ -> false)
                     | _ -> false)
            | _ -> false
        if elems |> List.forall isDeferredElem then elems |> List.collect (emitTupleParts c)
        else
            let rec forcePart (p: ValKind) =
                match p with
                | VArray ({ Src = (AVirt _ | AVirtFlat _) } as a) -> VArray (materialize c a)
                | VTuple ps -> VTuple (ps |> List.map forcePart)
                | other -> other
            // A tuple ON THE SPINE (Ctx.Spine: main's statement value, a
            // function body's, or a spine let's value): each member runs as
            // its own positional statement, so a module force inside member k
            // (a positional read `later(0)`) is hoisted to member k's start --
            // after members 0 .. k-1 -- not to the statement's start, ahead of
            // an earlier failing member (tuples/038, 043).
            // In MAIN only a tuple the C++ lane member-orders takes member
            // points (review codex-diff-W4B-B-3 finding 2): the statement's
            // root value or its let chain's tail (Ctx.TupleRoots) -- never an
            // intermediate let initializer, which the whole-statement
            // pre-pass covers -- and only under the C++ admission
            // (tupleNeedsMemberOrder: a structural member, or at least two
            // leaves that may fail).
            let memberPoints =
                if c.SpinePositional then onSpine
                else c.TupleRoots.Contains(box e) && tupleNeedsMemberOrderLl c elems
            if not c.SpinePositional && obj.ReferenceEquals(c.TupleTail, e) && c.StmtStart >= 0 then
                positionalStatement c
            elems |> List.collect (fun x ->
                if memberPoints then positionalStatement c
                emitTupleParts c x |> List.map forcePart)
    // A tuple-valued `if` (gap G59): the condition, then ONLY the taken arm's
    // pack -- its members evaluated left to right inside its own block, as the
    // array-valued `if` in emitArr builds each arm in place -- and one slot per
    // component, joined after the arms. Arms must agree component by
    // component (scalar types; array element types and static shapes).
    | IRIf (cond, tb, fb) ->
        let cv = coerce c ScBool (emitExpr c cond)
        let lThen = freshLbl c "tupif.then"
        let lElse = freshLbl c "tupif.else"
        let lEnd = freshLbl c "tupif.end"
        ln c (renderBr { Cond = cv.Reg; IfTrue = lThen; IfFalse = lElse })
        let stored (p: ValKind) =
            match p with
            | VArray a ->
                let a = materialize c a
                (match a.Src with
                 | APool _ -> VArray a
                 | _ -> refuse "an unmaterialized array component of an if-held tuple")
            | VScalar _ as s -> s
            | VTuple _ -> refuse "a nested pack component of an if-held tuple"
            | VOpaque -> refuse "an opaque (loop object / callable) component of an if-held tuple"
        lbl c lThen
        let tParts = emitTupleParts c tb |> List.map stored
        let slots =
            tParts |> List.map (fun p ->
                match p with
                | VArray a ->
                    let s = allocaOf c "ptr"
                    (match a.Src with APool ptr -> ln c ($"store ptr {ptr}, ptr {s}") | _ -> ())
                    Some s
                | VScalar v ->
                    let s = allocSlot c v.Ty
                    storeSlot c s v.Ty v
                    s
                | VTuple _ | VOpaque -> None)
        ln c (sprintf "br label %%%s" lEnd)
        lbl c lElse
        let fParts = emitTupleParts c fb |> List.map stored
        if fParts.Length <> tParts.Length then
            refuse "an if-held tuple whose arms have different component counts"
        List.zip3 tParts fParts slots |> List.iter (fun (tp, fp, slot) ->
            match tp, fp, slot with
            | VArray a, VArray b, Some s when a.Elem = b.Elem && a.Groups = b.Groups ->
                (match b.Src with APool ptr -> ln c ($"store ptr {ptr}, ptr {s}") | _ -> ())
            | VScalar a, VScalar b, s when a.Ty = b.Ty -> storeSlot c s b.Ty b
            | _ -> refuse "an if-held tuple whose arms differ in a component's type or shape")
        ln c (sprintf "br label %%%s" lEnd)
        lbl c lEnd
        List.zip tParts slots |> List.map (fun (tp, slot) ->
            match tp, slot with
            | VArray a, Some s ->
                let p = freshReg c
                ln c ($"{p} = load ptr, ptr {s}")
                VArray { a with Src = APool p }
            | VScalar v, Some s -> VScalar (loadSlot c s v.Ty)
            | VScalar v, None -> VScalar v
            | _ -> refuse "unreachable: an if-held tuple component without a slot")
    | IRParallel (l, r, _) -> emitTupleParts c l @ emitTupleParts c r
    // `<&!>` is MANDATORY fusion: legs whose loop extents differ cannot share
    // a nest, and the compiler must say so rather than quietly emit two
    // independent loops. Refusing here hands the program to the C++ lane,
    // which owns the diagnostic (corpus loops/065 pins it as REJECT-AT
    // codegen).
    | IRFusion (l, r) ->
        let parts = emitTupleParts c l @ emitTupleParts c r
        let shapes = parts |> List.choose (function VArray a -> Some a.Extents | _ -> None) |> List.distinct
        if List.length shapes > 1 then
            refuse "a mandatory fusion (<&!>) whose legs iterate different extents"
        parts
    | IRVar (id, _) when c.TupleSlots.ContainsKey id -> c.TupleSlots.[id]
    | IRReduceCompute (comp, k, i) -> emitReduceCompute c comp k i |> List.map VScalar
    // `|> compute` on a pack forces EVERY leg. The fused spelling differs from
    // the independent one only in how many traversals the C++ lane runs, not
    // in what the legs answer, so both land here.
    | IRCompute inner ->
        emitTupleParts c inner
        |> List.map (fun p -> match p with VArray a -> VArray (materialize c a) | other -> other)
    | IRPure inner -> emitTupleParts c inner
    | IRLet (id, v, body) -> bindLetIn c id v body; emitTupleParts c body
    | IRTupleProj (parent, idx, isFlat) ->
        (match tupleComponent c parent idx isFlat with
         | VTuple ps -> ps
         | single -> [ single ])
    // A TUPLE-RETURNING CALL (`let (a, b) = f()`, G12). This lane has no
    // struct-return ABI, so the callee is INLINED, exactly as a capturing
    // callee is in emitCall: its parameters bind to the arguments, and its
    // body is walked here as a pack, so the components land as the caller's
    // own values. A recursive tuple-returning call cannot be inlined and
    // refuses (the C++ lane takes it). Scalar and array tuple members must
    // fall through to classifyValue: their ordinary call ABI owns scoped
    // cleanup and escaping array results, even when the call is a tuple leaf.
    | IRApp (IRVar (fid, _), args, retType)
        when c.Callables.ContainsKey fid && (match stripUnits retType with IRTTuple _ -> true | _ -> false) ->
        let cl = c.Callables.[fid]
        if cl.IsArityPoly then refuse ($"a call to the arity-polymorphic function '{cl.SourceName}'")
        if cl.IsCudaKernel || cl.IsMpiParallel then
            refuse ($"a call to '{cl.SourceName}', which carries a cuda/mpi strategy")
        if List.length cl.Params <> List.length args then
            refuse ($"a partial application of '{cl.SourceName}' ({(List.length args)} of {(List.length cl.Params)} arguments)")
        if not (c.Inlining.Add cl.Id) then
            refuse ($"a recursive call to '{cl.SourceName}', which returns a tuple (inlined in this lane)")
        try
            let kargs =
                List.zip cl.Params args
                |> List.map (fun (p, a) ->
                    match arrayShapeOf p.Type with
                    | Some _ -> KArray (emitArr c a)
                    | None when (raggedArrShape p.Type).IsSome -> KArray (emitArr c a)
                    | None -> KScalar (emitExpr c a))
            List.iter2 (bindKernelParam c) cl.Params kargs
            // The inlined BODY is the function the C++ lane calls, so a guard
            // panic in it is located at the FUNCTION's declaration (gap G76;
            // withCalleeDecl). The arguments above are the CALLER's and keep
            // its span.
            withCalleeDecl c cl (fun () ->
                framePush c cl.SourceName
                let parts = emitTupleParts c cl.Body
                framePop c
                parts)
        finally c.Inlining.Remove cl.Id |> ignore
    // TERMINATION: the base case must NOT hand a pack-typed node back to
    // `classifyValue`, whose `IRTTuple` arm calls straight back here -- that
    // pair loops forever on any pack shape without an arm above (a
    // tuple-returning call has no struct-return ABI in this lane).
    | other ->
        match Blade.IR.stripUnits (typeOf other) with
        | IRTTuple _ ->
            refuse ($"the IR node {(caseName other)} in pack position -- the llvm lane builds packs from tuples, <&>/<&!> legs and reduction joins only")
        | _ -> [ classifyValue c other ]

/// A call whose RESULT is an array. The ABI is the same one every array uses:
/// the callee returns the pool pointer, and the caller reads the shape off the
/// declared return type -- no descriptor struct, per plan section 5.
and private emitArrCall (c: Ctx) (e: IRExpr) : ArrVal =
    match e with
    | IRApp (func, args, retType) ->
        let (elem, groups) = requireArray "an array-returning call" retType
        let v = emitCall c func args
        { Elem = elem; Groups = groups; Src = APool v.Reg; RowOpBytes = 0L }
    | _ -> refuse "a non-call in array-call position"

// ---------------------------------------------------------------------------
// Function and program assembly
// ---------------------------------------------------------------------------

/// How one function parameter crosses the ABI.
type private ParamKind =
    | PScalar of Sc
    | PArray of Sc * Grp list
    /// The ragged pair: pool pointer plus its offsets-table pointer, two
    /// arguments -- the "carry the per-row shape across the boundary as a
    /// separate pointer" ABI, spelled with offsets rather than lens because
    /// offsets are what addressing consumes and offsets[rows] is the total.
    | PRagged of Sc * int64

/// A03 consumes only an existing return-donor plan in an ordinary outlined
/// function. This is a realization check over actual slots/owned allocations,
/// not a new liveness or alias analysis. In particular AVirt operands are not
/// forced speculatively: their late evaluation could still read the old donor.
let private returnPoolDestination (c: Ctx) (cl: IRCallable) (lets: Map<IRId, IRExpr>)
                                  (tail: IRExpr) (donor: IRId) : Result<ApplyInfo * string * string, string> =
    let rec computedMap e =
        match e with
        | IRCompute (IRApplyCombinator info) -> Some info
        | IRCompute (IRCompute _ as inner) -> computedMap inner
        | _ -> None
    let plainShape ty =
        // No outer/element unit erasure or virtual/layout conversion. Axis
        // Id/Tag identify logical types; only their already-planned physical
        // extent is reused. The producer/result keep their original metadata.
        match ty with
        | ArrayElem at when not at.IsVirtual && at.ElemType = IRTScalar ETFloat64 ->
            match at.IndexTypes with
            | [ix] when ix.IxKind = IxKPlain && ix.Symmetry = SymNone && ix.Rank = 1 &&
                        ix.Kind = SDimension && ix.Dependencies.IsEmpty ->
                match ix.Extent with IRLit (IRLitInt n) when n > 0L -> Some n | _ -> None
            | _ -> None
        | _ -> None
    let mutable blocked : string option = None
    let require ok why = if not ok && blocked.IsNone then blocked <- Some why
    require (not (containsPoolLifetimeEvent cl.Body)) "inline lifetime region remains excluded"
    require (cl.Captures.IsEmpty && not (c.Facts.SelfRecursive.Contains cl.Id) &&
             not cl.IsOmpParallel && not cl.IsCudaKernel && not cl.IsMpiParallel)
            "the callable is not an ordinary capture-free nonrecursive serial function"
    let mutable letPlan = false
    iterIRExpr (function
        | IRLet (id, _, _) when (Blade.Types.PoolReuseTable.tryDonor id).IsSome -> letPlan <- true
        | _ -> ()) cl.Body
    require (not letPlan) "let-donor plans are outside the return-only LLVM consumer"
    let info = computedMap tail
    require info.IsSome "the return is not a computed scalar map"
    let donorValue = Map.tryFind donor lets
    require (donorValue |> Option.bind computedMap |> Option.isSome) "the donor is not a direct fresh computed body let"
    let n = plainShape cl.RetType
    require n.IsSome "the return is not static plain rank-one Float64 storage"
    require ((donorValue |> Option.bind (typeOf >> plainShape)) = n && n.IsSome) "the donor type/extent does not match the return"
    let scope = if c.PoolScopes.Count = 1 then Some c.PoolScopes.[0] else None
    require (scope |> Option.exists (fun s -> s.InlineId.IsNone && not (s.KnownIds.Contains donor)))
            "the donor has no exact current ordinary function scope"
    let donorArray = match c.ArrSlots.TryGetValue donor with true, a -> Some a | _ -> None
    let donorPtr = donorArray |> Option.bind (fun a -> match a.Src with APool p -> Some p | _ -> None)
    require donorPtr.IsSome "the donor is not a completed stored backing"
    let ownedSlot =
        match scope, donorPtr with
        | Some sc, Some ptr ->
            let entries = sc.Pools |> Seq.filter (fun (p, _) -> p = ptr) |> List.ofSeq
            match entries with [(_, slot)] -> Some slot | _ -> None
        | _ -> None
    require ownedSlot.IsSome "the donor has no unique owned allocation/tracking slot"
    let matchesShape (a: ArrVal) = a.Elem = ScF64 && (match a.Groups, n with [GDense m], Some n -> m = n | _ -> false)
    require (donorArray |> Option.exists matchesShape) "the donor backing layout does not match the return"
    match info with
    | Some info ->
        require (plainShape info.OutputType = n && n.IsSome && info.KernelInputRanks = [0] &&
                 info.KernelOutputRank = 0 && info.SDimsPerArray = [1] && info.KernelTDims.IsEmpty &&
                 not info.HasReynolds && (info.TriangularLevels |> List.forall not))
                "the return map has unsupported shape/peel/strategy metadata"
        match info.Arrays with
        | [IRVar (input, inputType)] ->
            require (plainShape inputType = n && n.IsSome) "the input type/extent does not match the return"
            match c.ArrSlots.TryGetValue input with
            | true, a ->
                require (matchesShape a) "the input backing layout does not match the return"
                match a.Src with
                | APool ptr ->
                    require (donorPtr <> Some ptr) "the input reads the donor backing"
                    let owned = scope |> Option.exists (fun sc -> sc.Pools |> Seq.filter (fun (p, _) -> p = ptr) |> Seq.length = 1)
                    let param = cl.Params |> List.mapi (fun i p -> p.VarId = input && ptr = sprintf "%%a%d" i) |> List.exists id
                    require (owned || param) "the input is not an independent owned root or original formal array"
                | _ -> require false "the input is deferred: no speculative forcing for return reuse"
            | _ -> require false "the input has no completed array slot"
        | _ -> require false "the return map does not have one named stored operand"
        match info.Kernel with
        | IRVar (kid, _) when c.Callables.ContainsKey kid ->
            let k = c.Callables.[kid]
            let snapshot (cap: CaptureInfo) =
                cap.Type = IRTScalar ETFloat64 &&
                (match c.Slots.TryGetValue cap.Id with true, (_, ScF64) -> true | _ -> false) &&
                ((Map.tryFind cap.Id lets |> Option.exists (fun v -> typeOf v = IRTScalar ETFloat64)) ||
                 (cl.Params |> List.exists (fun p -> p.VarId = cap.Id && p.Type = IRTScalar ETFloat64)))
            let vars = (k.Params |> List.map (_.VarId)) @ (k.Captures |> List.map (_.Id)) |> Set.ofList
            let rec closed e =
                match e with
                | IRLit (IRLitFloat _) -> true
                | IRVar (id, ty) -> ty = IRTScalar ETFloat64 && Set.contains id vars
                | IRBinOp (IRElementwise, (IRAdd | IRSub | IRMul | IRDiv), l, r, _) -> closed l && closed r
                | _ -> false
            require (k.Params.Length = 1 && (k.Params |> List.forall (fun p -> p.Type = IRTScalar ETFloat64)) &&
                     k.RetType = IRTScalar ETFloat64 && (k.Captures |> List.forall snapshot) && closed k.Body &&
                     not k.IsOmpParallel && not k.IsCudaKernel && not k.IsMpiParallel && not k.IsArityPoly)
                    "the return kernel is not closed scalar arithmetic over completed snapshots"
        | _ -> require false "the return kernel is not a direct resolved scalar callable"
    | None -> ()
    match blocked, info, donorPtr, ownedSlot with
    | None, Some info, Some ptr, Some slot -> Ok (info, ptr, slot)
    | Some why, _, _, _ -> Error why
    | _ -> Error "the return donor lacks a complete backing proof"

/// A04's certificate is the ONE shared semantic proof (Blade.SameCellReturn,
/// extracted unchanged so the C++ lane's A06 realization reads the same
/// admission); this lane passes its own callables table and recursion fact.
type private SameCellReturn = Blade.SameCellReturn.Certificate

let private sameCellReturnCertificate (c: Ctx) (cl: IRCallable) : Result<SameCellReturn, string> =
    Blade.SameCellReturn.certify c.Callables (fun id -> c.Facts.SelfRecursive.Contains id) cl

/// Read-only realization check. No forcing, allocation, slot mutation or early
/// free happens speculatively; fallback evaluates the original tail once.
let private sameCellReturnBacking (c: Ctx) (proof: SameCellReturn) : Result<string * string, string> =
    if c.PoolScopes.Count <> 1 then Error "no exact ordinary function scope"
    else
        let scope = c.PoolScopes.[0]
        match c.ArrSlots.TryGetValue proof.Donor, List.ofSeq scope.Pools with
        | (true, a), [(ptr, slot)] when scope.InlineId.IsNone && not (scope.KnownIds.Contains proof.Donor) &&
                                       a.Elem = ScF64 && a.Groups = [GDense proof.Extent] && a.RowOpBytes = 0L &&
                                       (match a.Src with APool p -> p = ptr | _ -> false) ->
            let tracking =
                c.Body |> Seq.map (fun line -> line.Trim())
                |> Seq.filter (fun line -> line.StartsWith "store ptr " && line.EndsWith (", ptr " + slot)) |> List.ofSeq
            let alias = c.ArrSlots |> Seq.exists (fun kv -> kv.Key <> proof.Donor && (match kv.Value.Src with APool p -> p = ptr | _ -> false))
            let complete = proof.Snapshots |> Set.forall (fun id -> match c.Slots.TryGetValue id with true, (_, ScF64) -> true | _ -> false)
            if alias || tracking <> [$"store ptr {ptr}, ptr {slot}"] then Error "donor ownership/slot was aliased, escaped or released"
            elif not complete then Error "scalar snapshot is not completed in this frame"
            else Ok (ptr, slot)
        | _ -> Error "donor is not the sole completed owned backing/tracking entry"

/// `internal` linkage everywhere but `main`: the module is the whole program
/// (Blade has no separate compilation), so nothing needs to be externally
/// visible and every definition stays available for inlining and DCE.
let private emitFunctionBody (c: Ctx) (cl: IRCallable) (sym: string) : string list =
    c.Body <- ResizeArray()
    c.Allocas <- ResizeArray()
    c.Slots <- Dictionary()
    c.ArrSlots <- Dictionary()
    c.NameSlots <- Dictionary()
    c.NameArrSlots <- Dictionary()
    // These two hold main's REGISTERS too; a function must not see them.
    c.TupleSlots <- Dictionary()
    c.GroupKeys.Clear()
    c.InFunction <- true
    c.StmtStart <- -1
    setSpine c None false
    // Guard panics in this body are located at the function's declaration
    // (CodeGenState.emittedDeclSpan: its own span, else its source's).
    c.DeclSpan <-
        (let s = Blade.IR.declSpanOf cl.Name
         if s.StartLine > 0 then s else Blade.IR.declSpanOf cl.SourceName)
    c.DeclName <- (if String.IsNullOrEmpty cl.SourceName then cl.Name else cl.SourceName)
    if not (List.isEmpty cl.Captures) then
        refuse ($"the function '{cl.Name}', which captures enclosing bindings")
    // The module bindings this body can read, rebound to their globals: a
    // scalar's slot IS its global, an array's pool pointer is loaded once in
    // the entry block (every caller proved it published; see emitCall).
    (match c.ModuleReads.TryGetValue cl.Id with
     | true, reads ->
         for rid in reads do
             match c.Published.TryGetValue rid with
             | true, PubScalar (sym, sc) -> c.Slots.[rid] <- (sym, sc)
             | true, PubArray (sym, elem, groups) ->
                 let r = freshReg c
                 c.Allocas.Add($"  {r} = load ptr, ptr {sym}")
                 c.ArrSlots.[rid] <- { Elem = elem; Groups = groups; Src = APool r; RowOpBytes = 0L }
             | _ -> ()
     | _ -> ())
    if (raggedArrShape cl.RetType).IsSome then
        refuse ($"'{cl.Name}' returns a ragged array (ragged returns are not in the llvm lane yet)")
    // An array parameter arrives as a bare `ptr` with its shape baked from the
    // declared type -- never a descriptor struct (plan section 5). A `mut`
    // parameter needs no separate treatment: the pointer IS the caller's pool,
    // so an element write already aliases.
    let ps =
        cl.Params
        |> List.mapi (fun i p ->
            match arrayShapeOf p.Type with
            | Some (elem, groups) -> (i, p, PArray (elem, groups))
            | None ->
                match raggedArrShape p.Type with
                | Some (elem, rows) -> (i, p, PRagged (elem, rows))
                | None ->
                    let sc = requireScalar ($"parameter '{p.Name}' of '{cl.Name}'") p.Type
                    if sc = ScVoid then refuse ($"a unit parameter on '{cl.Name}'")
                    (i, p, PScalar sc))
    // Parameter facts. `noundef` on everything (no undef/poison channel exists
    // in this emitter); `readonly` on pointers only when a whole-module scan
    // found no element write anywhere -- see `ModuleFacts.ArraysReadOnly`.
    // NOT `noalias`: two array arguments at one call site may be the SAME
    // pool (`f(a, a)` is ordinary Blade, and array identity is what licenses
    // symmetric storage in the first place), so a parameter is exactly where
    // that claim would be false.
    let arrayParamAttrs =
        if not (factEnabled factParamAttrs) then ""
        elif c.Facts.ArraysReadOnly then " noundef readonly"
        else " noundef"
    let signature =
        ps
        |> List.map (fun (i, _, k) ->
            match k with
            | PScalar sc -> sprintf "%s%s %%a%d" (llTy sc) (paramAttr ()) i
            | PArray _ -> sprintf "ptr%s %%a%d" arrayParamAttrs i
            | PRagged _ -> sprintf "ptr%s %%a%d, ptr%s %%a%dr" arrayParamAttrs i arrayParamAttrs i)
        |> String.concat ", "
    // NOT `noalias` on an array RETURN either: a body whose result is one of
    // its own parameters returns that pointer unchanged (`materialize` of an
    // already-stored array is the identity), so the returned pool is not
    // always fresh.
    let fnAttrs =
        attrRef c
            (if c.Facts.SelfRecursive.Contains cl.Id then grpFnRecursive
             elif c.Facts.AnyRecursion then grpFnNoRecurse
             else grpFnTerminating)
    for (i, p, k) in ps do
        match k with
        | PScalar sc ->
            let slot = allocaOf c (llTy sc)
            ln c (sprintf "store %s %%a%d, ptr %s" (llTy sc) i slot)
            c.Slots.[p.VarId] <- (slot, sc)
            c.NameSlots.[p.Name] <- (slot, sc)
        | PArray (elem, groups) ->
            let a = { Elem = elem; Groups = groups; Src = APool (sprintf "%%a%d" i); RowOpBytes = 0L }
            c.ArrSlots.[p.VarId] <- a
            c.NameArrSlots.[p.Name] <- a
        | PRagged (elem, rows) ->
            let a = { Elem = elem
                      Groups = [ GRagged (string rows, RtDynamic (sprintf "%%a%dr" i)) ]
                      Src = APool (sprintf "%%a%d" i); RowOpBytes = 0L }
            c.ArrSlots.[p.VarId] <- a
            c.NameArrSlots.[p.Name] <- a
    // A function body is a tracking scope: its temporaries die at `ret`, and
    // a function cannot capture (refused above), so nothing but the returned
    // pool can outlive the frame. The per-cell call was the arena model's
    // worst edge -- every temp a kernel-position callee materialized leaked
    // once per output cell.
    framePush c cl.SourceName
    pushPoolScope c
    // DEFERRED MODULE MAPS this body reads (gap G75, formalism 10.2): each is
    // evaluated PRIVATELY, inside each call -- bound here, in declaration
    // order, as the producer its module value classifies to over this body's
    // views of the published inputs, and AFTER the frame's scope opened, so a
    // force of it inside the body is a pool the frame owns and frees at its
    // exit (keepPool hands one a younger loop scope allocates back to it).
    // Never stored under the module name, never printed (main's prints are
    // already emitted, and a function body's force registers nothing there);
    // emitCall admitted every root call only while main had not forced it.
    (match c.ModuleReads.TryGetValue cl.Id with
     | true, reads ->
         let privateMaps =
             reads
             |> Seq.filter (fun rid -> c.DeferredModule.ContainsKey rid && not (c.Published.ContainsKey rid))
             |> Seq.sortBy (fun rid -> c.DeferredModule.[rid])
             |> List.ofSeq
         for rid in privateMaps do
             match Map.tryFind rid c.BindingValues with
             | Some v ->
                 (match classifyValue c v with
                  | VArray a -> bindArray c rid "" a
                  | _ -> refuse ($"'{cl.Name}' reads the deferred module binding '{defaultArg (Map.tryFind rid c.ModuleNames) (string rid)}', whose value is not an array producer in a function body"))
             | None -> ()
     | _ -> ())
    // The body's statements form a SPINE (Ctx.Spine), each a positional
    // statement: a force of a private map is stored at the start of the
    // statement that first forces it, with the frame's lifetime (the frame's
    // scope the only one open -- forceHoistable), after every effect an
    // earlier statement of the body emitted.
    c.StmtStart <- c.Body.Count
    c.StmtSlots <- Dictionary(c.ArrSlots)
    c.HoistedIds.Clear()
    setSpine c (Some cl.Body) true
    match arrayShapeOf cl.RetType with
    | Some (elem, groups) ->
        // An array-returning function hands back the pool pointer. The result
        // must be MATERIALIZED before it leaves: a producer's closure captures
        // this frame's slots, which do not survive the return.
        let result =
            match Blade.Types.PoolReuseTable.tryReturnDonor cl.Id with
            | None ->
                let record outcome evidence =
                    Blade.Effects.Decisions.record
                        { Rule = "same-cell-return-llvm"; Version = 1; Span = c.DeclSpan; Subject = cl.SourceName
                          Outcome = outcome; Evidence = ($"function v{cl.Id}; LLVM-only certificate (no generic return donor)" :: evidence) }
                match sameCellReturnCertificate c cl with
                | Error why ->
                    record (Blade.Effects.Declined why) []
                    materialize c (emitArr c cl.Body)
                | Ok proof ->
                    if proof.Function <> cl.Id || proof.Body <> cl.Body then
                        refuse "same-cell return: certificate belongs to a different function/body"
                    let rec bindOriginal e =
                        match e with
                        | IRLet (id, value, body) ->
                            bindLetIn c id value body
                            bindOriginal body
                        | _ -> ()
                    bindOriginal proof.Body
                    match sameCellReturnBacking c proof with
                    | Error why ->
                        record (Blade.Effects.Declined why) []
                        materialize c (emitArr c (IRCompute (IRApplyCombinator proof.Tail)))
                    | Ok (ptr, slot) ->
                        let producer = applyToArr c proof.Tail
                        match producer.Src with
                        | AVirt _ when producer.Elem = ScF64 && producer.Groups = groups ->
                            ln c ($"; A04: same-cell return: function v{proof.Function} donor v{proof.Donor} backing {ptr} slot {slot}")
                            let result = fillDensePool c ptr producer
                            record Blade.Effects.Applied [$"donor v{proof.Donor}, owned backing {ptr}, tracking slot {slot}; old cell loaded before same-index store"]
                            result
                        | _ -> refuse "same-cell return: prepared producer contradicts its closed dense certificate"
            | Some donor ->
                let record outcome evidence =
                    Blade.Effects.Decisions.record
                        { Rule = "pool-reuse-llvm"; Version = 1; Span = c.DeclSpan; Subject = cl.SourceName
                          Outcome = outcome; Evidence = ($"function v{cl.Id}, return donor v{donor}" :: evidence) }
                let rec emitReturn lets tail =
                    match tail with
                    | IRLet (id, value, body) ->
                        bindLetIn c id value body
                        emitReturn (Map.add id value lets) body
                    | tail ->
                        match returnPoolDestination c cl lets tail donor with
                        | Error why ->
                            record (Blade.Effects.Declined why) []
                            materialize c (emitArr c tail)
                        | Ok (info, ptr, slot) ->
                            // Preflight emitted nothing and proved every input
                            // already stored; prepare the original producer ONCE.
                            let producer = applyToArr c info
                            match producer.Src with
                            | AVirt _ when producer.Elem = ScF64 && producer.Groups = groups ->
                                ln c ($"; A03: return pool reuse: function v{cl.Id} donor v{donor} backing {ptr} slot {slot}")
                                let result = fillDensePool c ptr producer
                                record Blade.Effects.Applied [ $"owned backing {ptr}, tracking slot {slot}; completed independent input; original dense fill" ]
                                result
                            | _ -> refuse "return pool reuse: prepared producer contradicts its dense scalar preflight"
                emitReturn Map.empty cl.Body
        if result.Elem <> elem || result.Groups <> groups then
            refuse ($"the body of '{cl.Name}' produces a shape its return type does not declare")
        (match result.Src with
         | APool p ->
             // Free the frame's temps, sparing the result. What the result IS
             // decides how much can be freed: a pool this frame allocated
             // (spared by pointer compare -- a branch join may alias it to any
             // tracked pool); a parameter (nothing of this frame escapes); or
             // UNKNOWN provenance -- a loaded slot, a row-view GEP whose
             // backing pool must stay live -- where freeing anything would
             // need an escape argument this emitter does not have, so the
             // scope drops its records and keeps today's arena behavior.
             let tracked =
                 c.PoolScopes.[c.PoolScopes.Count - 1].Pools
                 |> Seq.exists (fun (q, _) -> q = p)
             let isParam =
                 p.StartsWith "%a" && p.Length > 2 && p.[2..] |> Seq.forall System.Char.IsDigit
             if tracked then popPoolScope c (Some p) true
             elif isParam then popPoolScope c None true
             else popPoolScope c None false
             framePop c
             ln c ($"ret ptr {p}")
         | _ -> refuse "an unmaterialized array return")
        [ yield $$"""define internal ptr {{sym}}({{signature}}){{fnAttrs}} {"""
          yield "entry:"
          yield! c.Allocas
          yield! c.Body
          yield "}" ]
    | None ->
    let retTy = requireScalar ($"the return type of '{cl.Name}'") cl.RetType
    let result = emitExpr c cl.Body
    if retTy = ScVoid then
        popPoolScope c None true
        framePop c
        ln c "ret void"
    else
        let result = coerce c retTy result
        popPoolScope c None true
        framePop c
        ln c ($"ret {(llTy retTy)} {result.Reg}")
    [ yield $$"""define internal {{(llTy retTy)}} {{sym}}({{signature}}){{fnAttrs}} {"""
      yield "entry:"
      yield! c.Allocas
      yield! c.Body
      yield "}" ]

/// Merge every module into one, exactly as `genSelfContainedProgramFromIR`
/// does before handing the C++ emitter a single module: ids are program-global,
/// so concatenation preserves both declaration order and every metadata key.
let private mergeModules (modules: IRModule list) : IRModule =
    match modules with
    | [ m ] -> m
    | ms ->
        { Name = "merged"
          Types = ms |> List.collect (_.Types)
          Functions = ms |> List.collect (_.Functions)
          Bindings = ms |> List.collect (_.Bindings)
          StaticFunctionUsage = ms |> List.fold (fun acc m -> Map.fold (fun a k v -> Map.add k v a) acc m.StaticFunctionUsage) Map.empty
          ProviderReads = ms |> List.fold (fun acc m -> Map.fold (fun a k v -> Map.add k v a) acc m.ProviderReads) Map.empty
          ProviderWrites = ms |> List.fold (fun acc m -> Map.fold (fun a k v -> Map.add k v a) acc m.ProviderWrites) Map.empty
          RandomInits = ms |> List.fold (fun acc m -> Map.fold (fun a k v -> Map.add k v a) acc m.RandomInits) Map.empty
          CompoundInits = ms |> List.fold (fun acc m -> Map.fold (fun a k v -> Map.add k v a) acc m.CompoundInits) Map.empty
          SparseInits = ms |> List.fold (fun acc m -> Map.fold (fun a k v -> Map.add k v a) acc m.SparseInits) Map.empty
          MutableArrayLets = ms |> List.fold (fun acc m -> Set.union acc m.MutableArrayLets) Set.empty
          DerivedFuncOrigins = ms |> List.fold (fun acc m -> Map.fold (fun a k v -> Map.add k v a) acc m.DerivedFuncOrigins) Map.empty
          FunctionAliasNames = ms |> List.fold (fun acc m -> Set.union acc m.FunctionAliasNames) Set.empty }

/// Module-level metadata the lane has no lowering for. Checked FIRST, before a
/// single instruction is built, so the refusal names the feature rather than
/// whatever expression happens to trip over it.
let private checkModuleScope (m: IRModule) : unit =
    if not (Map.isEmpty m.ProviderReads) then refuse "a provider read (netcdf/zarr/csv/sql)"
    if not (Map.isEmpty m.ProviderWrites) then refuse "a provider write (netcdf/zarr/csv/sql)"
    if not (Map.isEmpty m.RandomInits) then refuse "a random-fill initializer"
    // A compound-index initializer is emitted per binding (emitCompoundInit:
    // the rank-1 compaction) and refuses there for every other shape.
    if not (Map.isEmpty m.SparseInits) then refuse "a sparse-index initializer"
    for td in m.Types do
        match td with
        // Index-type and alias declarations are typecheck-time identity; they
        // carry no storage and reach the back end as nothing at all.
        // An INTEGER EnumIdx's values are keys represented as positions (D2):
        // printing and every observation read the key table, which this lane
        // does not build -- the C++ lane takes the program.
        | IRTDEnumIdx (name, _, values)
            when not values.IsEmpty && values |> List.forall (function EVInt _ -> true | _ -> false) ->
            refuse ($"the integer EnumIdx type '{name}'")
        | IRTDAlias _ | IRTDIndexType _ | IRTDEnumIdx _ -> ()
        // A struct DECLARATION needs no runtime representation of its own
        // (idx_card and friends fold to scalars before this lane runs). A
        // struct VALUE still refuses wherever one reaches emission -- the
        // scalar/array type gates do not admit IRTStruct -- so this claims
        // no layout or calling convention.
        | IRTDStruct _ -> ()
        | IRTDVariant (name, _) -> refuse ($"the variant type '{name}'")

// ---------------------------------------------------------------------------
// Printing
//
// EVERY corpus EXPECT is a byte pin against what the C++ lane prints, so the
// shapes below are transcriptions of `CodeGen.genPrintNested2` /
// `genPrintArrayFlat`, not re-derivations: ONE regime for every rank -- rank 2
// nests (`[[a, b], [c, d]]`, the shape its literal is written in), every other
// rank prints FLAT as one row-major comma-separated run.
// ---------------------------------------------------------------------------

let private outStr (c: Ctx) (s: string) : unit =
    needShim c "blade_out_str"
    ln c (renderCall { Dest = None; RetTy = ScVoid; Callee = "@blade_out_str"
                       Args = [ ScStr, stringGlobal c s ] })

let private outVal (c: Ctx) (v: Val) : unit =
    match v.Ty with
    | ScI64 ->
        needShim c "blade_out_i64"
        ln c (renderCall { Dest = None; RetTy = ScVoid; Callee = "@blade_out_i64"; Args = [ ScI64, v.Reg ] })
    | ScF64 ->
        needShim c "blade_out_f64"
        ln c (renderCall { Dest = None; RetTy = ScVoid; Callee = "@blade_out_f64"; Args = [ ScF64, v.Reg ] })
    | ScStr ->
        needShim c "blade_out_str"
        ln c (renderCall { Dest = None; RetTy = ScVoid; Callee = "@blade_out_str"; Args = [ ScStr, v.Reg ] })
    | ScBool ->
        // C has no i1 ABI: the flag widens at the call and the shim prints
        // boolalpha's "true"/"false".
        needShim c "blade_out_bool"
        let w = freshReg c
        ln c ($"{w} = zext i1 {v.Reg} to i32")
        ln c ($"call void @blade_out_bool(i32 {w})")
    | ScVoid -> ()

let private emitIfThen (c: Ctx) (cond: string) (thenPart: unit -> unit) : unit =
    let lThen = freshLbl c "when.then"
    let lEnd = freshLbl c "when.end"
    ln c (renderBr { Cond = cond; IfTrue = lThen; IfFalse = lEnd })
    lbl c lThen
    thenPart ()
    ln c (sprintf "br label %%%s" lEnd)
    lbl c lEnd

/// `if (!first) cout << ", "; first = false;` -- the separator discipline both
/// C++ printers use, so a leading element never gets a comma.
let private emitSeparator (c: Ctx) (flag: string) : unit =
    let f = freshReg c
    ln c ($"{f} = load i1, ptr {flag}")
    let nf = freshReg c
    ln c (renderBin { Dest = nf; Opcode = "xor"; Flags = ""; Ty = ScBool; Lhs = f; Rhs = "true" })
    emitIfThen c nf (fun () -> outStr c ", ")
    ln c ($"store i1 false, ptr {flag}")

/// The C++ lane's `genPrintArraySymAware` in LLVM: ONE regime for every rank
/// (rank 2 nests, everything else runs flat), with the per-axis bound of a
/// compact group SHRUNK by the prior axes of its own group -- `extents[1] - i`
/// symmetric, `- i - 1` strict. The inner loop variable is the STORAGE
/// coordinate (the left-justified slot), which is exactly what `arr[i][j]`
/// indexes on the C++ side; that is what makes a printed triangle the same
/// bytes in both lanes, which every symmetric corpus EXPECT depends on.
/// `pre` / `post` frame the bracketed values: `name = [` / `]\n` for a
/// binding's own line, `[` / `]` for a component inside a tuple's line.
let private emitArrayPrintGeneric (c: Ctx) (pre: string) (post: string) (a: ArrVal) : unit =
    let rank = a.Rank
    /// The literal bound of axis `d` given the loop registers already open.
    /// Only an axis INSIDE a compact group has a dependent bound.
    let axisPlan =
        [ for g in a.Groups do
            for k in 0 .. grpRank g - 1 do
                yield (grpExtent g, (if grpCompact g then k else 0), grpStrict g) ]
    let boundOf (d: int) (prior: string list) : string =
        let (ext, groupPos, strict) = axisPlan.[d]
        if groupPos = 0 then string ext
        else
            // extent - (the group's prior storage coordinates) - strict*pos.
            let baseVal = ext - (if strict then int64 groupPos else 0L)
            let mutable acc = string baseVal
            for p in prior |> List.skip (d - groupPos) do
                acc <- i64Bin c "sub" acc p
            acc
    outStr c pre
    if rank = 2 then
        emitCountedLoop c a.Extents.[0] (fun i ->
            let t = freshReg c
            ln c (renderCmp { Dest = t; Kind = "icmp"; Pred = "ne"; Ty = ScI64; Lhs = i; Rhs = "0" })
            emitIfThen c t (fun () -> outStr c ", ")
            outStr c "["
            let flag = allocaOf c "i1"
            ln c ($"store i1 true, ptr {flag}")
            emitCountedLoopTo c (boundOf 1 [ i ]) (fun j ->
                emitSeparator c flag
                outVal c (readCell c a [ { Reg = i; Ty = ScI64 }; { Reg = j; Ty = ScI64 } ]))
            outStr c "]")
    else
        let flag = allocaOf c "i1"
        ln c ($"store i1 true, ptr {flag}")
        let rec go (d: int) (prior: string list) =
            if d = rank then
                emitSeparator c flag
                outVal c (readCell c a (prior |> List.map (fun r -> { Reg = r; Ty = ScI64 })))
            else
                emitCountedLoopTo c (boundOf d prior) (fun i -> go (d + 1) (prior @ [ i ]))
        go 0 []
    outStr c post

/// The print dispatcher. Ragged shapes carry operand bounds the generic
/// printer's static axis plan cannot express, so they get their own arms --
/// the SAME bytes the C++ lane's Ragged<T> printer produces: rank 2 nests
/// with the per-row bound, a peeled row runs flat.
let private emitArrayPrintFramed (c: Ctx) (pre: string) (post: string) (a: ArrVal) : unit =
    match a.Groups with
    | [ GRagged (rows, table) ] ->
        outStr c pre
        emitCountedLoopTo c rows (fun i ->
            let t = freshReg c
            ln c (renderCmp { Dest = t; Kind = "icmp"; Pred = "ne"; Ty = ScI64; Lhs = i; Rhs = "0" })
            emitIfThen c t (fun () -> outStr c ", ")
            outStr c "["
            let flag = allocaOf c "i1"
            ln c ($"store i1 true, ptr {flag}")
            emitCountedLoopTo c (raggedLenAt c table i) (fun j ->
                emitSeparator c flag
                outVal c (readCell c a [ { Reg = i; Ty = ScI64 }; { Reg = j; Ty = ScI64 } ]))
            outStr c "]")
        outStr c post
    // A rank-1 compound prints its kept cells in storage order as one flat
    // run (G22: the C++ lane's genPrintStatements compound arm).
    | [ GDynDense len ] | [ GCompact (len, _) ] ->
        outStr c pre
        let flag = allocaOf c "i1"
        ln c ($"store i1 true, ptr {flag}")
        emitCountedLoopTo c len (fun j ->
            emitSeparator c flag
            outVal c (readCell c a [ { Reg = j; Ty = ScI64 } ]))
        outStr c post
    | _ -> emitArrayPrintGeneric c pre post a

let private emitArrayPrint (c: Ctx) (label: string) (a: ArrVal) : unit =
    // A domain (sparse-shaped) array has no printed form in the C++ lane
    // (`Sparse<T,RANK>` has no operator<<; genPrintStatements notes it in a
    // comment), so it prints nothing here either.
    if a.Groups |> List.exists (function GDomain _ -> true | _ -> false) then ()
    else
    emitArrayPrintFramed c (label + " = [") "]\n" a

/// A top-level TUPLE's line, spelled like its literal: the twin of
/// `CodeGen.genPrintAggregate` (same classification, `aggregatePrintOf`),
/// over the pack the binding holds. The parentheses follow the TYPE's nesting;
/// the components are taken left to right from the pack, which is FLAT for a
/// `<&>` / `<&!>` chain (its type is the left-nested pair tree) and may carry
/// a nested pack for a nested literal -- both are consumed here. A component
/// this lane cannot print the C++ lane's way refuses, so the program falls
/// back to the C++ lane rather than printing a different line.
let private emitTuplePrint (c: Ctx) (label: string) (ap: Blade.CodeGen.AggregatePrint) (parts: ValKind list) : unit =
    let bad what = refuse ($"the print of tuple '{label}': {what}")
    // Print `ap` from the front of `rest`, answering what is left.
    let rec tuple (aps: Blade.CodeGen.AggregatePrint list) (rest: ValKind list) : ValKind list =
        outStr c "("
        let rest' =
            aps |> List.indexed |> List.fold (fun r (i, p) ->
                if i > 0 then outStr c ", "
                go p r) rest
        outStr c ")"
        rest'
    and go (ap: Blade.CodeGen.AggregatePrint) (rest: ValKind list) : ValKind list =
        match ap, rest with
        | Blade.CodeGen.APTuple aps, VTuple inner :: tail ->
            (match tuple aps inner with
             | [] -> tail
             | _ -> bad "a nested pack does not match its tuple component")
        | Blade.CodeGen.APTuple aps, _ -> tuple aps rest
        | (Blade.CodeGen.APScalar | Blade.CodeGen.APKeyed _), VScalar v :: tail when v.Ty <> ScVoid -> outVal c v; tail
        | (Blade.CodeGen.APDenseArray _ | Blade.CodeGen.APKeyedArray _), VArray a :: tail -> emitArrayPrintFramed c "[" "]" a; tail
        | Blade.CodeGen.APOpaque text, _ :: tail -> outStr c text; tail
        | _, [] -> bad "the pack has fewer components than the tuple's type"
        | _ -> bad "a component has no print in this lane"
    match ap with
    | Blade.CodeGen.APTuple aps ->
        outStr c (label + " = ")
        (match tuple aps parts with
         | [] -> outStr c "\n"
         | _ -> bad "the pack has more components than the tuple's type")
    | _ -> bad "not a tuple"

/// Which bindings never print, mirroring `CodeGen.computeDeferredIds`: an
/// unforced computation has no materialized array to echo. A binding a
/// consumer LATER forced is a real array by program end and prints like any
/// eager one -- that is what `Ctx.Forced` records.
let private deferredBindingIds (bindings: IRBinding list) : HashSet<IRId> =
    let ids = HashSet<IRId>()
    let rec deferredValue (inner: Set<IRId>) (ty: IRType) (value: IRExpr) : bool =
        let isDeferredExpr (e: IRExpr) =
            match e with
            | IRApplyCombinator _ | IRComposeApply _ | IRParallel _ | IRFusion _ | IRFunctorMap _
            | IRChoice _ | IRFallback _ | IRComposeObj _ | IRComposeMeth _ | IRBind _ | IRZip _
            | IRSequence _ -> true
            | IRVar (id, _) -> ids.Contains id || Set.contains id inner
            | _ -> false
        let resultIsArray = match Blade.IR.stripUnits ty with ArrayElem _ -> true | _ -> false
        match value with
        // A let chain defers when its final value does (computeDeferredIds'
        // IRLet arm): IRLift hoists an inline operand of a deferred binding.
        | IRLet _ ->
            let (lets, finalExpr) = Blade.IRLift.peelLetChain value
            let inner' =
                lets |> List.fold (fun acc (id, lty, v) ->
                    if deferredValue acc lty v then Set.add id acc else acc) inner
            deferredValue inner' ty finalExpr
        | _ ->
            match value with
            | IRApplyCombinator _ | IRComposeApply _ | IRParallel _ | IRFusion _ -> true
            | IRZip _ | IRComposeObj _ | IRFallback _ -> true
            | IRBind (comp, _) -> resultIsArray || isDeferredExpr comp
            | IRComposeMeth (l, r) -> resultIsArray || isDeferredExpr l || isDeferredExpr r
            | IRFunctorMap (_, inner) -> resultIsArray || isDeferredExpr inner
            | IRChoice (l, r) -> resultIsArray || isDeferredExpr l || isDeferredExpr r
            | IRGuard (_, body) -> resultIsArray || isDeferredExpr body
            | IRSequence elems -> resultIsArray || (elems |> List.exists isDeferredExpr)
            | IRTuple elems -> elems |> List.forall isDeferredExpr
            | IRTupleProj (IRVar (pid, _), _, _) -> ids.Contains pid || Set.contains pid inner
            | IRVar (srcId, _) -> ids.Contains srcId || Set.contains srcId inner
            | _ -> false
    for b in bindings do
        if deferredValue Set.empty b.Type b.Value then ids.Add b.Id |> ignore
    ids

/// `|> compute` of a deferred combinator is a forced materialization and
/// always prints; `|> compute` of anything else prints exactly when the
/// wrapped value would. Unmaterialized loop values never print.
let rec private printableValue (v: IRExpr) : bool =
    match v with
    | IRCompute (IRApplyCombinator _ | IRComposeApply _ | IRParallel _ | IRFusion _ | IRVar _ | IRFunctorMap _ | IRChoice _ | IRFallback _ | IRComposeMeth _ | IRBind _ | IRGuard _ | IRSequence _) -> true
    | IRCompute inner -> printableValue inner
    | IRMethodFor _ | IRObjectFor _ -> false
    | _ -> true

/// The auto-print pass, mirroring `CodeGen.genPrintStatements`: top-level
/// bindings echo in DECLARATION order, after the timing line, one
/// `<name> = <value>` per printable binding.
let private emitPrints (c: Ctx) (bindings: IRBinding list) : unit =
    let deferred = deferredBindingIds bindings
    // ALIAS CLASSES (gap G60, formalism 10.2; IRLift.deferredAliasClasses, the
    // C++ lane's and the interpreter's classification): a deferred map and its
    // top-level aliases are ONE value, so once any member was forced every
    // member prints (an alias's slot is its source's producer, or the pool a
    // force upgraded it to -- the same cells: writes through them are refused).
    let forcedOwn = HashSet<IRId>(c.Forced)
    Blade.IRLift.deferredAliasClasses bindings (Blade.IRLift.computeDeferredIds bindings)
    |> Map.iter (fun m cls -> if cls |> List.exists forcedOwn.Contains then c.Forced.Add m |> ignore)
    // The TYPES genPrintStatements has an arm for. A loop object, a function
    // alias, a tuple pack and a `Computation<T>` wrapper all fall off the end
    // of its match and print nothing -- `let x = pure(42)` echoes no line.
    let printableType (t: IRType) =
        match Blade.IR.stripUnits t with
        | IRTScalar (ETFloat64 | ETFloat32 | ETInt64 | ETInt32 | ETBool | ETComplex64 | ETComplex128 | ETString) -> true
        | IRTIdxTagged _ | IRTNat _ -> true
        | ArrayElem _ -> true
        // A tuple prints when it has a print form (genPrintStatements'
        // aggregate arm); a destructuring carrier never does.
        | IRTTuple _ -> true
        | _ -> false
    // `--print <names>` (BLADE_PRINT, CodeGenState.printSelection), applied
    // exactly where genPrintStatements applies it (G12): a selection prints
    // only the named bindings, internal `__` names included; no selection
    // prints every non-internal one. This pass used to ignore the selection
    // and echo every binding.
    let selection = Blade.CodeGenState.printSelection ()
    let wanted (b: IRBinding) =
        match selection with
        | Some names -> Set.contains b.Name names
        | None -> not (Blade.CodeGen.isInternalBindingName b.Name)
    let printsHere (b: IRBinding) =
        (not (deferred.Contains b.Id) || c.Forced.Contains b.Id)
        && printableValue b.Value && printableType b.Type
        && (match c.Slots.TryGetValue b.Id with
            | true, (_, sc) -> sc <> ScVoid
            | _ ->
                c.ArrSlots.ContainsKey b.Id
                || (match c.TupleSlots.TryGetValue b.Id with
                    // A struct component reaches the print below, which
                    // refuses it (the C++ lane prints it).
                    | true, _ -> not (Blade.CodeGen.isDestructureCarrier b)
                                 && ((Blade.CodeGen.aggregatePrintOf [] b.Type).IsSome
                                     || Blade.IR.flattenTupleLeaves b.Type
                                        |> List.exists (fun t -> (Blade.IR.stripUnits t).IsIRTNamed))
                    | _ -> false))
    // A selected name this pass would NOT print -- no such binding (a typo),
    // a function, a deferred loop value, a value with no printed form -- is
    // the C++ lane's BL7004 refusal, by name, with its exact text: hand the
    // program over rather than restate (or silently drop) that judgment.
    match selection with
    | Some names ->
        for n in names do
            if not (bindings |> List.exists (fun b -> b.Name = n && printsHere b)) then
                refuse ($"the --print selection names '{n}', which this lane does not print (the C++ lane prints it or refuses BL7004)")
    | None -> ()
    for b in bindings do
        let visible = wanted b && printsHere b
        if visible then
            match c.Slots.TryGetValue b.Id with
            | true, (slot, sc) when sc <> ScVoid ->
                let v = loadSlot c slot sc
                let label = stringGlobal c b.Name
                let callPrint fn argTy argReg =
                    needShim c fn
                    ln c (renderCall { Dest = None; RetTy = ScVoid; Callee = "@" + fn
                                       Args = [ ScStr, label; argTy, argReg ] })
                match sc with
                | ScI64 -> callPrint "blade_print_i64" ScI64 v.Reg
                | ScF64 -> callPrint "blade_print_f64" ScF64 v.Reg
                | ScStr -> callPrint "blade_print_str" ScStr v.Reg
                | ScBool ->
                    // The shim's parameter is a C `int`: C has no i1 ABI, so the
                    // boolean widens at the call and the shim prints boolalpha.
                    needShim c "blade_print_bool"
                    let w = freshReg c
                    ln c ($"{w} = zext i1 {v.Reg} to i32")
                    ln c ($"call void @blade_print_bool(ptr {label}, i32 {w})")
                | ScVoid -> ()
            | _ ->
                match c.ArrSlots.TryGetValue b.Id with
                | true, a -> emitArrayPrint c b.Name a
                | _ ->
                    match c.TupleSlots.TryGetValue b.Id with
                    | true, parts when not (Blade.CodeGen.isDestructureCarrier b) ->
                        match Blade.CodeGen.aggregatePrintOf [] b.Type with
                        | Some ap -> emitTuplePrint c b.Name ap parts
                        // No type defs here: a tuple holding a STRUCT reads as
                        // formless, but the C++ lane prints it -- refuse
                        // rather than print nothing.
                        | None when Blade.IR.flattenTupleLeaves b.Type
                                    |> List.exists (fun t -> (Blade.IR.stripUnits t).IsIRTNamed) ->
                            refuse ($"the print of tuple '{b.Name}': a struct component")
                        | None -> ()
                    | _ -> ()

/// `let S = compound(dense, mask)` -- THE RANK-1 COMPACTION (bench gap G9),
/// the twin of the C++ lane's genCompoundInitBinding over compound_index_t<1>
/// and of the interpreter's buildCompound, in IR:
///
///   1. the mask is a stored Bool pool over ONE dense axis of n cells (the
///      binding's type says so: a single IxKCompound slot of rank 1 and no
///      trailing dims; the mask's shape says the axis is dense);
///   2. COUNT: m = sum of the mask bytes (one counted loop, an i64 accumulator
///      the vectorizer folds);
///   3. FILL: the kept-position table, `pos[q] = i; q += keep[i]` over i
///      ascending -- the branchless form, one slot of slack; kept order IS
///      source order, so every lane agrees on it;
///   4. GATHER: dst[q] = src[pos[q]] for q in [0, m) -- exactly m cells
///      moved, monotone reads, no predicate read.
///
/// The value is `GCompact (m, pos)` over the dst pool: every flat consumer
/// (fold, print, extents, a map) treats it as a runtime-extent rank-1 array;
/// the keyed read `S(k)` binary-searches `pos` (emitRaw). The hashed index
/// stays the C++ lane's: a rank >= 2 mask, trailing dims, a mask over anything
/// but a dense axis, or a non-scalar element refuse here by name. Serial in
/// every build -- a parallel prefix would be a different algorithm, and the
/// kernel protocol (bench maskagg spec, M5) keeps compaction sequential.
/// The FUSED rank-1 compaction (Blade.Optimize.planCompactionFusion, B4b/c;
/// the C++ lane's genFusedCompaction): m (counted by the mask's own pass,
/// tryBindCountedMask) sizes both pools at m + 1, and ONE pass over i
/// ascending gathers `dst[q] = src[i]`, fills `pos[q] = i` and advances
/// `q += keep[i]` -- branchless, stable; the last cell is the slack the
/// unconditional stores need, never exposed (the value is `GCompact (m,
/// pos)`, m cells).
///
/// B4c: a planned fold over `y * y` this lane would NOT decorate (the serial
/// chain: BLADE_FP_REPRO=1, or no licence) is fed inside the pass after every
/// 256-cell block, over completed cells only (index < q): the first kept
/// square peeled as the chain's start, then `acc = k(acc, v * v)` in kept
/// order. The product carries NO fast-math flag, so it cannot contract into
/// the add -- the rounding a stored `y * y` cell has, under every contraction
/// mode. The fold's own site keeps the BL8003 empty check (emitReduce).
///
/// Round 3 (compaction fusion v2), as in the C++ lane:
///   * `positions = false` (T4b): no position pool, no `pos[q] = i` store;
///     the value is `GCompact (m, compactNoPositions)`, which the keyed-read
///     arm refuses to look up (the census proved there is none);
///   * `gathered = Some g` (T4a): the mask's pass already filled g's pools
///     (and fed the chains); this site loads them -- after running the B4
///     pass itself into the same slots and accumulators when g's run-time
///     guard is false (n over the selectivity cap).
let private emitFusedCompaction (c: Ctx) (b: IRBinding) (elem: Sc) (n: string) (keepPool: string) (srcPool: string) (cntSlot: string) (folds: Blade.Optimize.CompactionFoldPlan list) (positions: bool) (gathered: Blade.Optimize.CompactionGatherLlvm option) : unit =
    // B4's one fill+gather pass into fresh m + 1 pools, written through the
    // given slots, feeding the given chains.
    let b4Pass (dstSlot: string) (posSlot: string option) (feeds: (IRCallable * string) list) =
        let m = (loadSlot c cntSlot ScI64).Reg
        let mSlack = i64Add c m "1"
        let pos =
            posSlot |> Option.map (fun s ->
                let p = allocPool c ScI64 mSlack
                ln c ($"store ptr {p}, ptr {s}")
                p)
        let dst = allocPool c elem mSlack
        ln c ($"store ptr {dst}, ptr {dstSlot}")
        let cursor = allocaOf c "i64"
        ln c ($"store i64 0, ptr {cursor}")
        emitCompactionPassLlvm c n dst cursor feeds (fun i ->
            let q = loadSlot c cursor ScI64
            let v = loadCell c elem (gepCell c elem srcPool i)
            storeCell c elem (gepCell c elem dst q.Reg) v
            pos |> Option.iter (fun p -> storeCell c ScI64 (gepCell c ScI64 p q.Reg) { Reg = i; Ty = ScI64 })
            let bit = loadCell c ScBool (gepCell c ScBool keepPool i)
            let w = freshReg c
            ln c ($"{w} = zext i1 {bit.Reg} to i64")
            let next = i64Add c q.Reg w
            ln c ($"store i64 {next}, ptr {cursor}"))
    let (dstSlot, posSlot) =
        match gathered with
        | Some g ->
            (match g.Guard with
             | None -> ()
             | Some guard ->
                 let feeds =
                     g.Feeds |> List.choose (fun (mid, acc) ->
                         folds |> List.tryFind (fun f -> f.MapId = mid)
                         |> Option.bind (fun f -> resolveCallable f.FoldKernel)
                         |> Option.map (fun cl -> (cl, acc)))
                 let lHave = freshLbl c "cgather.have"
                 let lBuild = freshLbl c "cgather.build"
                 ln c (renderBr { Cond = guard; IfTrue = lHave; IfFalse = lBuild })
                 lbl c lBuild
                 b4Pass g.DstSlot g.PosSlot feeds
                 ln c (sprintf "br label %%%s" lHave)
                 lbl c lHave)
            (g.DstSlot, g.PosSlot)
        | None ->
            let dstSlot = allocaOf c "ptr"
            let posSlot = if positions then Some (allocaOf c "ptr") else None
            let feeds = if elem <> ScF64 then [] else compactionFeedsLlvm c folds
            b4Pass dstSlot posSlot (feeds |> List.map (fun (_, cl, acc) -> (cl, acc)))
            for (f, _, acc) in feeds do f.EmittedLlvm.Value <- Some acc
            (dstSlot, posSlot)
    let m = (loadSlot c cntSlot ScI64).Reg
    let dst = freshReg c
    ln c ($"{dst} = load ptr, ptr {dstSlot}")
    let pos =
        match posSlot with
        | Some s ->
            let p = freshReg c
            ln c ($"{p} = load ptr, ptr {s}")
            p
        | None -> compactNoPositions
    bindArray c b.Id b.Name { Elem = elem; Groups = [ GCompact (m, pos) ]; Src = APool dst; RowOpBytes = 0L }

/// A companion of a SHARED SELECTION (Blade.Optimize.planCompactionFusion,
/// T4c; the C++ lane's genSharedCompanion). The FIRST companion completes the
/// selection: ONE branchless pass over the source axis gathers every
/// companion's pool (`v_j[q] = c_j[i]`, `pos[q] = i` when any companion
/// demands the positions, `q += keep[i]`; m + 1 cells per pool, the slack
/// never exposed) and feeds every companion's in-pass serial chains -- unless
/// the mask's own pass already gathered (T4a), when it runs that pass only
/// while the run-time cap guard is false. Every companion binds `GCompact (m,
/// pos)` over ITS OWN pool with the SHARED count and positions (one
/// selection; main's pools have program lifetime, so nothing is freed here,
/// for any companion). False: the selection is unavailable here, and the
/// caller takes its ordinary path.
let private emitSharedCompanion (c: Ctx) (b: IRBinding) (elem: Sc) (n: string) (keepPool: string) (srcPool: string) (p: Blade.Optimize.CompactionPlan) (s: Blade.Optimize.SharedSelection) : bool =
    let (firstId, _, _) = List.head s.Members
    let bindFrom (dstSlot: string) (posSlot: string option) (cntSlot: string) =
        let m = (loadSlot c cntSlot ScI64).Reg
        let dst = freshReg c
        ln c ($"{dst} = load ptr, ptr {dstSlot}")
        let pos =
            match posSlot with
            | Some sl ->
                let r = freshReg c
                ln c ($"{r} = load ptr, ptr {sl}")
                r
            | None -> compactNoPositions
        bindArray c b.Id b.Name { Elem = elem; Groups = [ GCompact (m, pos) ]; Src = APool dst; RowOpBytes = 0L }
    if b.Id <> firstId then
        match s.BuiltLlvm.Value with
        | Some bl when bl.Ready && bl.DstSlots.ContainsKey b.Id ->
            ln c ($"; compaction fusion v2, shared selection (T4c): `{b.Name}` binds its own pool and the shared count and positions")
            let (lastId, _, _) = List.last s.Members
            if b.Id = lastId then
                ln c ($"; T4c lifetime: the shared count and positions are owned by the plan, never by a companion (each binds only its own pool; `{b.Name}` is the last); main's pools keep program lifetime (compound() is top-level only, BL6002): no free is emitted for any companion or for the selection")
            bindFrom bl.DstSlots.[b.Id] bl.PosSlot bl.CountSlot
            true
        | _ -> false
    else
    // NOT BUILT in this lane for a RUN-TIME extent: the lane refuses runtime
    // ranges, so no supported program can exercise the shared selection's
    // dynamic arm here (neither the joint pass nor the guarded mask-site
    // gather); fail closed -- every companion takes its own passes -- and
    // record why.
    if not (fst (System.Int64.TryParse n)) then
        if Blade.Effects.Decisions.active () then
            Blade.Effects.Decisions.record
                { Rule = "compaction-fusion"; Version = 2; Span = c.DeclSpan; Subject = b.Name
                  Outcome = Blade.Effects.Declined "llvm lane: the shared selection over a run-time extent is not built in this lane (unverified: no supported program reaches it); each companion keeps its own count / fill / gather passes"
                  Evidence = [ "fail closed: the C++ lane builds the run-time arm (the guarded joint pass)" ] }
        false
    else
    match p.CountLlvm.Value with
    | None -> false
    | Some cntSlot ->
    let shapeOk (v: ArrVal) =
        match v.Groups with
        | [ GDense k ] -> string k = n
        | [ GDynDense l ] -> l = n
        | _ -> false
    let srcs =
        s.Members |> List.map (fun (cid, _, src) ->
            let el = scalarTyOf s.ElemTypes.[cid]
            let pool =
                if cid = b.Id then Some srcPool
                else
                    match c.ArrSlots.TryGetValue src with
                    | true, v ->
                        (match v.Src, el with
                         | APool ptr, Some e when v.Elem = e && shapeOk v -> Some ptr
                         | _ -> None)
                    | _ -> None
            (cid, el, pool))
    let usable =
        srcs |> List.forall (fun (_, el, pool) ->
            pool.IsSome && (match el with Some (ScI64 | ScF64 | ScBool) -> true | _ -> false))
    if not usable then false
    else
    let srcs = srcs |> List.map (fun (cid, el, pool) -> (cid, el.Value, pool.Value))
    let built = s.BuiltLlvm.Value |> Option.filter (fun bl -> bl.AtMask)
    let dstSlots =
        match built with
        | Some bl -> bl.DstSlots
        | None -> srcs |> List.map (fun (cid, _, _) -> (cid, allocaOf c "ptr")) |> Map.ofList
    let posSlot =
        match built with
        | Some bl -> bl.PosSlot
        | None -> if s.PositionsNeeded then Some (allocaOf c "ptr") else None
    // The joint pass into fresh m + 1 pools written through the slots,
    // feeding the given chains (kernel, accumulator, companion).
    let jointPass (feeds: (IRCallable * string * IRId) list) =
        let m = (loadSlot c cntSlot ScI64).Reg
        let mSlack = i64Add c m "1"
        let pos =
            posSlot |> Option.map (fun sl ->
                let pp = allocPool c ScI64 mSlack
                ln c ($"store ptr {pp}, ptr {sl}")
                pp)
        let dsts =
            srcs |> List.map (fun (cid, el, _) ->
                let d = allocPool c el mSlack
                ln c ($"store ptr {d}, ptr {dstSlots.[cid]}")
                (cid, d)) |> Map.ofList
        let cursor = allocaOf c "i64"
        ln c ($"store i64 0, ptr {cursor}")
        emitCompactionPassLlvmMulti c n cursor (feeds |> List.map (fun (k, acc, cid) -> (k, acc, dsts.[cid]))) (fun i ->
            let q = loadSlot c cursor ScI64
            for (cid, el, sp) in srcs do
                let v = loadCell c el (gepCell c el sp i)
                storeCell c el (gepCell c el dsts.[cid] q.Reg) v
            pos |> Option.iter (fun pp -> storeCell c ScI64 (gepCell c ScI64 pp q.Reg) { Reg = i; Ty = ScI64 })
            let bit = loadCell c ScBool (gepCell c ScBool keepPool i)
            let w = freshReg c
            ln c ($"{w} = zext i1 {bit.Reg} to i64")
            let next = i64Add c q.Reg w
            ln c ($"store i64 {next}, ptr {cursor}"))
    (match built with
     | Some bl ->
         match bl.Guard with
         | None -> ()
         | Some guard ->
             let feeds =
                 bl.Feeds |> List.choose (fun (mid, acc, cid) ->
                     Blade.Optimize.CompactionTable.foldsOf cid |> List.tryFind (fun f -> f.MapId = mid)
                     |> Option.bind (fun f -> resolveCallable f.FoldKernel)
                     |> Option.map (fun cl -> (cl, acc, cid)))
             let lHave = freshLbl c "csel.have"
             let lBuild = freshLbl c "csel.build"
             ln c (renderBr { Cond = guard; IfTrue = lHave; IfFalse = lBuild })
             lbl c lBuild
             jointPass feeds
             ln c (sprintf "br label %%%s" lHave)
             lbl c lHave
     | None ->
         let feeds =
             srcs |> List.collect (fun (cid, el, _) ->
                 if el <> ScF64 then []
                 else compactionFeedsLlvm c (Blade.Optimize.CompactionTable.foldsOf cid) |> List.map (fun (f, cl, acc) -> (f, cl, acc, cid)))
         jointPass (feeds |> List.map (fun (_, cl, acc, cid) -> (cl, acc, cid)))
         for (f, _, acc, _) in feeds do f.EmittedLlvm.Value <- Some acc)
    ln c ($"; compaction fusion v2, shared selection (T4c): ONE selection (count, positions) for {s.Members.Length} companions, completed at `{b.Name}`")
    if s.Members.Length = 1 then
        ln c ($"; T4c lifetime: the shared count and positions are owned by the plan, never by a companion (each binds only its own pool; `{b.Name}` is the last); main's pools keep program lifetime (compound() is top-level only, BL6002): no free is emitted for any companion or for the selection")
    s.BuiltLlvm.Value <-
        Some { Blade.Optimize.SharedSelectionLlvm.DstSlots = dstSlots
               Blade.Optimize.SharedSelectionLlvm.PosSlot = posSlot
               Blade.Optimize.SharedSelectionLlvm.CountSlot = cntSlot
               Blade.Optimize.SharedSelectionLlvm.Guard = (match built with Some bl -> bl.Guard | None -> None)
               Blade.Optimize.SharedSelectionLlvm.Feeds = (match built with Some bl -> bl.Feeds | None -> [])
               Blade.Optimize.SharedSelectionLlvm.AtMask = built.IsSome
               Blade.Optimize.SharedSelectionLlvm.Ready = true }
    bindFrom dstSlots.[b.Id] posSlot cntSlot
    true

let private emitCompoundInit (c: Ctx) (b: IRBinding) (denseExpr: IRExpr) (maskExpr: IRExpr) : unit =
    let arrTy =
        match Blade.IR.stripUnits b.Type with
        | ArrayElem arrTy -> arrTy
        | _ -> refuse "a compound-index initializer whose binding is not an array"
    (match arrTy.IndexTypes with
     | [ ix ] when ix.IxKind = IxKCompound && ix.Rank = 1 -> ()
     | [ _ ] when (Blade.Optimize.EquiJoinTable.tryCompound b.Id).IsSome ->
         // The hash equijoin (Blade.Optimize.planEquiJoin) is C++ v1: this
         // lane refuses the rank-2 compound as before, and says so for the
         // plan (the program falls back to the C++ lane, which places it).
         let why = "llvm lane: the hash equijoin is not built in this lane (v1.1)"
         if Blade.Effects.Decisions.active () then
             Blade.Effects.Decisions.record
                 { Rule = "hash-join"; Version = 1; Span = c.DeclSpan; Subject = b.Name
                   Outcome = Blade.Effects.Declined why
                   Evidence = [ "the rank-2 compound (the C++ lane's) is refused here; the C++ fallback emits the stable CSR and the left-major / right-minor probe" ] }
         refuse ($"a rank-2 compound-index initializer ({why}; the hashed index is the C++ lane's)")
     | [ ix ] when ix.IxKind = IxKCompound ->
         refuse ($"a rank-{ix.Rank} compound-index initializer (the hashed index is the C++ lane's)")
     | _ :: _ :: _ -> refuse "a compound-index initializer with trailing dims (the C++ lane's)"
     | _ -> refuse "a compound-index initializer of an unexpected shape")
    let elem =
        match scalarTyOf arrTy.ElemType with
        | Some (ScI64 | ScF64 | ScBool as e) -> e
        | _ -> refuse "a compound-index initializer over a non-Int64/Float64/Bool element"
    // The dense source, then the mask, both STORED (a deferred source is forced
    // under its own name, as the C++ lane's forceDeferredArrayInput does), in
    // that order -- the interpreter's (materializeCompoundBinding) and the C++
    // lane's (genCompoundInitBinding): with both operands computed in place,
    // a failure in the dense operand is the first failure, not one in the
    // mask (corpus sql-masks/017; this lane materialized the mask first).
    let src = materialize c (consumeArrOperand c denseExpr)
    let keep = materialize c (consumeArrOperand c maskExpr)
    if keep.Elem <> ScBool then refuse "a compound mask that is not a Bool array"
    let n =
        match keep.Groups with
        | [ GDense n ] -> string n
        | [ GDynDense len ] -> len
        | [ GCompact _ ] -> refuse "a compound of a compound (a mask over a compound's cells)"
        | _ -> refuse "a compound mask over other than one dense axis"
    if src.Groups <> keep.Groups then refuse "a compound whose dense operand and mask differ in shape"
    if src.Elem <> elem then refuse "a compound whose dense operand's element differs from its type"
    let keepPool, srcPool =
        match keep.Src, src.Src with
        | APool k, APool s -> k, s
        | _ -> refuse "unreachable: compound operands not materialized"
    // COMPACTION FUSION (Blade.Optimize.planCompactionFusion, B4b/c): the
    // mask's own pass counted (tryBindCountedMask published the slot), so the
    // fill and the gather are ONE pass; the twin of the C++ lane's
    // genFusedCompaction. Every planned fold over this compound resets its
    // handshake first -- unless the mask's pass already gathered (T4a) and
    // fed the chains it published.
    let folds = Blade.Optimize.CompactionTable.foldsOf b.Id
    let planHere =
        match Blade.Optimize.CompactionTable.tryCompound b.Id, maskExpr with
        | Some p, IRVar (mId, _) when mId = p.MaskId -> Some p
        | _ -> None
    // T4c: a companion of a shared selection (the first completes it, the
    // others bind their own pools over it; their chains were fed there).
    let sharedDone =
        match planHere with
        | Some p when p.Shared.IsSome -> emitSharedCompanion c b elem n keepPool srcPool p p.Shared.Value
        | _ -> false
    if sharedDone then () else
    let gathered = planHere |> Option.bind (fun p -> p.GatherLlvm.Value)
    if gathered.IsNone then
        for f in folds do f.EmittedLlvm.Value <- None
    let fusedCount = planHere |> Option.bind (fun p -> p.CountLlvm.Value)
    match fusedCount with
    | Some cntSlot ->
        let positions = planHere |> Option.forall (fun p -> p.PositionsNeeded)
        emitFusedCompaction c b elem n keepPool srcPool cntSlot folds positions gathered
    | None ->
    // 2. COUNT.
    let acc = allocaOf c "i64"
    ln c ($"store i64 0, ptr {acc}")
    emitCountedLoopTo c n (fun i ->
        let bit = loadCell c ScBool (gepCell c ScBool keepPool i)
        let w = freshReg c
        ln c ($"{w} = zext i1 {bit.Reg} to i64")
        let cur = loadSlot c acc ScI64
        let next = i64Add c cur.Reg w
        ln c ($"store i64 {next}, ptr {acc}"))
    let m = (loadSlot c acc ScI64).Reg
    // 3. FILL: the kept positions, one slack slot for the unconditional store.
    let mSlack = i64Add c m "1"
    let pos = allocPool c ScI64 mSlack
    let cursor = allocaOf c "i64"
    ln c ($"store i64 0, ptr {cursor}")
    emitCountedLoopTo c n (fun i ->
        let q = loadSlot c cursor ScI64
        storeCell c ScI64 (gepCell c ScI64 pos q.Reg) { Reg = i; Ty = ScI64 }
        let bit = loadCell c ScBool (gepCell c ScBool keepPool i)
        let w = freshReg c
        ln c ($"{w} = zext i1 {bit.Reg} to i64")
        let next = i64Add c q.Reg w
        ln c ($"store i64 {next}, ptr {cursor}"))
    // 4. GATHER by kept position into the exactly-sized compact pool.
    let dst = allocPool c elem m
    emitCountedLoopTo c m (fun q ->
        let p = loadCell c ScI64 (gepCell c ScI64 pos q)
        let v = loadCell c elem (gepCell c elem srcPool p.Reg)
        storeCell c elem (gepCell c elem dst q) v)
    bindArray c b.Id b.Name { Elem = elem; Groups = [ GCompact (m, pos) ]; Src = APool dst; RowOpBytes = 0L }

/// Emit `main`: bindings in declaration order, the `<name> completed in <t>s`
/// timing line, then the auto-print pass -- the exact statement order
/// `genMainWrapper` assembles for the C++ lane, which is what makes the two
/// lanes' stdout comparable line for line.
/// A TOP-LEVEL `|> compute` of a map FORCES its deferred array operands, as
/// the C++ lane's genComputeBinding does (it forces every deferred
/// `info.Arrays` entry under its own name before the nest renders, through
/// forceDeferredArrayInput, so at main's top level each joins the auto-print
/// list): formalism 10.2 -- a module-level forcing use forced a member, so the
/// alias class prints. This lane read such an operand where it stood (an outer
/// product's or a co-iteration's producer slot) and never marked it, so
/// `let P = method_for(g, y) <@> k |> compute` printed `P` without `g` (corpus
/// symmetry/147: `g`, `g2`, `g3` missing). Each operand now goes through the
/// shared any-order operand rule (consumeArrOperandAnyOrderTotal): forced into
/// a pool at the statement when it is read more than once or when in-place
/// consumption could be told apart (an assignment anywhere, a producer that
/// may fail), otherwise consumed in place and marked printable -- the
/// emission of a retained operand is unchanged. The value is resolved as
/// genComputeBinding's resolveComputation does: through `|> compute`
/// wrappers, functor maps and still-deferred module names (`let v = h |>
/// compute` forces h's operands, not h -- a copy, 10.2); an `object_for`
/// application's arguments count as its operands.
let private forceComputeOperandsAtTop (c: Ctx) (value: IRExpr) : unit =
    let isDeferredModule (id: IRId) =
        c.BindingValues.ContainsKey id
        && (match c.ArrSlots.TryGetValue id with
            | true, { Src = (AVirt _ | AVirtFlat _) } -> true
            | _ -> false)
    let rec resolve (seen: Set<IRId>) (e: IRExpr) : IRExpr list =
        match e with
        | IRCompute inner | IRFunctorMap (_, inner) -> resolve seen inner
        | IRVar (id, _) when isDeferredModule id && not (Set.contains id seen) ->
            resolve (Set.add id seen) c.BindingValues.[id]
        | IRApplyCombinator info -> info.Arrays
        | IRApp (IRObjectFor _, args, _) ->
            args |> List.collect (function IRTuple es -> es | a -> [ a ])
        | _ -> []
    match value with
    | IRCompute _ ->
        for operand in resolve Set.empty value do
            match operand with
            | IRVar (id, _) when isDeferredModule id -> consumeArrOperandAnyOrderTotal c operand |> ignore
            | _ -> ()
    | _ -> ()

let private emitMain (c: Ctx) (m: IRModule) (programName: string) : string list =
    c.Body <- ResizeArray()
    c.Allocas <- ResizeArray()
    c.Slots <- Dictionary()
    c.ArrSlots <- Dictionary()
    c.NameSlots <- Dictionary()
    c.NameArrSlots <- Dictionary()
    needShim c "blade_now"
    needShim c "blade_print_completed"
    c.BindingValues <- (m.Bindings |> List.map (fun b -> (b.Id, b.Value)) |> Map.ofList)
    let t0 = freshReg c
    ln c (renderCall { Dest = Some t0; RetTy = ScF64; Callee = "@blade_now"; Args = [] })
    for b in m.Bindings do
        // MEMBERSHIP HOISTING (Optimize.planMembershipHoist): each structure
        // planned for this host is built BEFORE the statement starts -- ahead
        // of StmtStart, so a force hoisted to the statement's start still
        // follows it -- over the set's stored pool, read once, into an
        // entry-block slot; it is live in MemberTables for this statement
        // only and freed right after it. A set this lane holds as anything
        // but a rank-1 Int64 pool builds nothing (its probes scan).
        let built = ResizeArray<IRId * string>()
        for p in Blade.Optimize.MemberHoistTable.plansOf b.Id do
            // FAIL CLOSED: the build needs the set's STORED rank-1 Int64 pool
            // here, before the host; anything else builds nothing (the probes
            // scan as written) and says so in the plan record.
            let pool =
                match c.ArrSlots.TryGetValue p.SetId with
                | true, a when a.Elem = ScI64 ->
                    match a.Src, a.Groups with
                    | APool ptr, [ GDense n ] -> Ok (ptr, string n)
                    | APool ptr, [ GDynDense len ] -> Ok (ptr, len)
                    | APool ptr, [ GCompact (len, _) ] -> Ok (ptr, len)
                    | APool _, _ -> Error "it is not a rank-1 pool in this lane"
                    | _ -> Error "it is a computed (unstored) array in this lane"
                | true, _ -> Error "its cells are not Int64 in this lane"
                | _ -> Error "it is not bound in this lane at the build point"
            match pool with
            | Ok (ptr, mCells) ->
                let slot = allocaOf c "[128 x i8], align 8"
                needShim c "blade_member_init_i64"
                needShim c "blade_member_note_llvm"
                c.AnyFrees <- true
                ln c ($"; member-hoist v1: `{p.HostName}`'s probes of `{p.SetName}` read ONE search structure built here (blade_member.h through the shim)")
                ln c ($"call void @blade_member_init_i64(ptr {slot}, ptr {ptr}, i64 {mCells}, i64 {p.Forced})")
                let hostG = stringGlobal c p.HostName
                let setG = stringGlobal c p.SetName
                ln c ($"call void @blade_member_note_llvm(ptr {slot}, ptr {hostG}, ptr {setG})")
                c.MemberTables.[p.SetId] <- slot
                built.Add((p.SetId, slot))
            | Error why ->
                if Blade.Effects.Decisions.active () then
                    Blade.Effects.Decisions.record
                        { Rule = "member-hoist"; Version = 1; Span = Blade.IR.declSpanOf b.Name
                          Subject = p.HostName
                          Outcome = Blade.Effects.Declined $"llvm lane: the planned build over `{p.SetName}` is not emitted -- {why}; the probes scan as written"
                          Evidence = [ "fail closed: no structure without the set's stored pool at the build point" ] }
        c.StmtStart <- c.Body.Count
        c.StmtSlots <- Dictionary(c.ArrSlots)
        c.HoistedIds.Clear()
        setSpine c (Some b.Value) false
        c.DeclSpan <- Blade.IR.declSpanOf b.Name
        c.DeclName <- b.Name
        // A BLOCK statement (a let chain) forces the C++ lane's pre-pass set
        // UP FRONT, in its order, before any block effect or alias upgrade
        // (genLetChainBinding's forceDeferredPositionalReads; review
        // codex-diff-W4B-B-4): a later read then sees the stored cells, an
        // alias of one of them only copies, and an alias's own force can no
        // longer suppress another map's statement-start force. A deferred map
        // whose in-place consumption nothing could tell apart (no assignment
        // anywhere, a producer that cannot fail) used to be left to its
        // reader; that exception is gone (see below).
        // No exception (review codex-diff-W4B-B-5 finding 1): EVERY collected
        // map is forced, in collector order -- a "total" map left to its
        // reader could still read a failing deferred input after a later
        // collected map's force.
        (match b.Value with
         | IRLet _ ->
             for x in c.StmtForceOrder do
                 match x with
                 | IRVar (id, _) when c.BindingValues.ContainsKey id ->
                     (match c.ArrSlots.TryGetValue id with
                      | true, { Src = (AVirt _ | AVirtFlat _) } -> materializeExpr c x |> ignore
                      | _ -> ())
                 | _ -> ()
         | _ -> ())
        (match Map.tryFind b.Id m.CompoundInits with
         | Some (denseExpr, maskExpr) -> emitCompoundInit c b denseExpr maskExpr
         | None ->
             forceComputeOperandsAtTop c b.Value
             bindNamed c b.Id b.Name b.Type b.Value)
        publishBinding c b.Id
        for (sid, slot) in built do
            needShim c "blade_member_fini_i64"
            ln c ($"call void @blade_member_fini_i64(ptr {slot})")
            c.MemberTables.Remove sid |> ignore
    c.StmtStart <- -1
    setSpine c None false
    let t1 = freshReg c
    ln c (renderCall { Dest = Some t1; RetTy = ScF64; Callee = "@blade_now"; Args = [] })
    let elapsed = freshReg c
    ln c (renderBin { Dest = elapsed; Opcode = "fsub"; Flags = ""; Ty = ScF64; Lhs = t1; Rhs = t0 })
    let nameG = stringGlobal c programName
    ln c (renderCall { Dest = None; RetTy = ScVoid; Callee = "@blade_print_completed"
                       Args = [ ScStr, nameG; ScF64, elapsed ] })
    emitPrints c m.Bindings
    ln c "ret i32 0"
    // `main` is the one EXTERNAL definition (everything else is `internal`),
    // and nothing calls it, so `norecurse` holds unconditionally; termination
    // is the module-wide question every other function faces.
    let mainAttrs = attrRef c (if c.Facts.AnyRecursion then grpFnNoRecurse else grpFnTerminating)
    [ yield $$"""define i32 @main(){{mainAttrs}} {"""
      yield "entry:"
      yield! c.Allocas
      yield! c.Body
      yield "}" ]

/// Resolve every frame marker in the emitted bodies (`main` and each
/// function, each a `define ... {` .. `}` line list): compute which module
/// functions can reach a panic, then turn each push/pop pair whose region can
/// into the shadow-stack stores the shim's panics read, and drop the rest.
let private resolveFrames (c: Ctx) (bodies: string list list) : string list list =
    let callRe = System.Text.RegularExpressions.Regex(@"@([A-Za-z0-9_$.\-]+)\(")
    let panickers = shimPanicSymbols
    let symOf (body: string list) =
        match body with
        | def :: _ -> (callRe.Match def).Groups.[1].Value
        | [] -> ""
    let syms = bodies |> List.map symOf |> Set.ofList
    // Per line: does it call a panicking shim symbol directly, and which module
    // functions does it call. The `define` line is the body's own symbol, not
    // a call, so it is skipped.
    let lineCalls (line: string) =
        let ms = callRe.Matches line
        [ for m in ms -> m.Groups.[1].Value ]
    let directOf (line: string) = lineCalls line |> List.exists panickers.Contains
    let calleesOf (line: string) = lineCalls line |> List.filter syms.Contains
    let facts =
        bodies
        |> List.map (fun body ->
            let rest = List.tail body
            symOf body, (rest |> List.exists directOf, rest |> List.collect calleesOf |> Set.ofList))
    // Least fixpoint of needs(f) = direct(f) || exists g in callees(f). needs(g):
    // a function that only recurses into itself does not force its own frame.
    let needs = HashSet<string>()
    for (s, (direct, _)) in facts do
        if direct then needs.Add s |> ignore
    let mutable changed = true
    while changed do
        changed <- false
        for (s, (_, callees)) in facts do
            if not (needs.Contains s) && callees |> Set.exists needs.Contains then
                needs.Add s |> ignore
                changed <- true
    let mutable anyFrame = false
    let resolved =
        bodies
        |> List.map (fun body ->
            let lines = List.toArray body
            // hot.[k]: line k reaches a panic. A region needs its frame when
            // any line inside it is hot; prefix sums answer that per pair.
            let prefix = Array.zeroCreate (lines.Length + 1)
            for k in 0 .. lines.Length - 1 do
                let l = lines.[k]
                let hot = k > 0 && (directOf l || calleesOf l |> List.exists needs.Contains)
                prefix.[k + 1] <- prefix.[k] + (if hot then 1 else 0)
            let keep = Array.create lines.Length false
            let opened = Stack<int>()
            for k in 0 .. lines.Length - 1 do
                if lines.[k].StartsWith framePushMark then opened.Push k
                elif lines.[k] = framePopMark then
                    let i = opened.Pop()
                    if prefix.[k] - prefix.[i + 1] > 0 then
                        keep.[i] <- true
                        keep.[k] <- true
            if opened.Count > 0 then failwith "EmitLlvm.resolveFrames: an unmatched frame push"
            [ for k in 0 .. lines.Length - 1 do
                let l = lines.[k]
                if l.StartsWith framePushMark then
                    if keep.[k] then
                        anyFrame <- true
                        // blade_rt::Scope's branchless store: overflow frames
                        // land in the sink slot 64, never a bounds branch.
                        let name = stringGlobal c (l.Substring framePushMark.Length)
                        let d, lt, ix, slot, d1 = freshReg c, freshReg c, freshReg c, freshReg c, freshReg c
                        yield $"  {d} = load i32, ptr @blade_frame_depth"
                        yield $"  {lt} = icmp slt i32 {d}, 64"
                        yield $"  {ix} = select i1 {lt}, i32 {d}, i32 64"
                        yield $"  {slot} = getelementptr inbounds [65 x ptr], ptr @blade_frame_stack, i64 0, i32 {ix}"
                        yield $"  store ptr {name}, ptr {slot}"
                        yield $"  {d1} = add i32 {d}, 1"
                        yield $"  store i32 {d1}, ptr @blade_frame_depth"
                elif l = framePopMark then
                    if keep.[k] then
                        let d, d1 = freshReg c, freshReg c
                        yield $"  {d} = load i32, ptr @blade_frame_depth"
                        yield $"  {d1} = add i32 {d}, -1"
                        yield $"  store i32 {d1}, ptr @blade_frame_depth"
                else yield l ])
    if anyFrame then
        c.Globals.Add "@blade_frame_depth = external global i32"
        c.Globals.Add "@blade_frame_stack = external global [65 x ptr]"
    resolved

// ---------------------------------------------------------------------------
// The omp census for this lane (bench gap G11)
//
// The lane has no thread runtime by design (nosync, no atomics, the shim has
// no threads), so EVERY `where omp` licence compiles serial here. The
// "a dropped omp is never silent" invariant still holds: the threading half is
// reported with the same census phrase the C++ lane uses
// (`[omp] requested but emitted serial: <reason>`) -- as a comment in the
// emitted `.ll`, in `blade plan`, and on the build's stderr. The reordering
// half of the licence is what the lane spends (`foldFmfLicensed` -> `reassoc
// nsz` on the accumulator chain), except in the REPRO build, which keeps every
// fold serial in both lanes.
// ---------------------------------------------------------------------------

/// The reason half of the lane's census line, for the current build.
let ompCensusReason () : string =
    if Blade.CodeGenState.fpReassocEnabled () then
        "the LLVM lane has no thread-level form; a licensed fold is reassociated instead (one whose kernel or deferred map writes keeps its serial chain)"
    else
        "the LLVM lane has no thread-level form, and BLADE_FP_REPRO=1 keeps every fold serial (no reassociation either)"

/// The full census phrase (same spelling as the C++ lane's
/// `ompSuppressedPhrase`, which is what lets one grep cover both lanes).
let ompCensusPhrase () : string =
    $"[omp] requested but emitted serial: {(ompCensusReason ())}"

/// The callables of `program` that carry an `omp` request, by the name the
/// program wrote (a lambda's is `lambda in <scope>`), deduplicated in order.
let ompRequestedKernels (program: IRProgram) : string list =
    program.Modules
    |> List.collect (fun m -> m.Functions)
    |> List.filter (fun f -> f.IsOmpParallel)
    |> List.map (fun f -> if String.IsNullOrEmpty f.SourceName then f.Name else f.SourceName)
    |> List.distinct

/// Emit the whole program as textual LLVM IR, or refuse it by name.
///
/// `programName` is the label the timing line carries -- the C++ lane uses the
/// source file's stem, so the router passes the same thing.
let tryEmitProgramNamed (programName: string) (program: IRProgram) : Result<string, string> =
    // A fresh BL7005 channel per program (the warnings of THIS lane's emission).
    Blade.Diagnostics.EmitterWarnings.reset ()
    try
        match program.Modules with
        | [] -> Error "an empty program"
        | modules ->
            let m = mergeModules modules
            checkModuleScope m
            let callables = Blade.IRPrint.buildCallablesTableForModule m
            // INSTALL the callables table for the whole emission, and restore
            // the caller's on the way out. `resolveKernel`, `resolveCallable`
            // and `buildLoopNestCodeGen` all read it out of the AsyncLocal
            // analysis context -- without this the loop builder resolves every
            // kernel to None and silently emits an empty nest.
            let prevCtx = Blade.IR.setCallablesContext callables
            // G41 / G43: the write analysis follows named deferred values
            // through this module's lets (installed per program, like the C++
            // lane's genModule).
            (Blade.CodeGenExprSupport.programLetValuesCell ()).Value <- Blade.CodeGenExprSupport.computeProgramLetValues m
            (Blade.CodeGenExprSupport.programCallbackArgsCell ()).Value <- Blade.CodeGenExprSupport.computeProgramCallbackArgs m
            // G46: which lets deep-copy (the write facts' CopiedLet)
            (Blade.CodeGenExprSupport.programMutableArrayLetsCell ()).Value <- m.MutableArrayLets
            try
            // The fact pass runs BEFORE any instruction: `main`'s own attribute
            // group depends on whether a function emitted much later recurses,
            // and its `define` line is finished the moment its body is.
            // Diagnostics escape hatch, off unless asked: the day the reduce
            // fusion landed was spent guessing at IR shapes this dump would
            // have shown in one look. stderr only; emission is unaffected.
            if System.Environment.GetEnvironmentVariable "BLADE_LLVM_DUMP_IR" = "1" then
                let names = Blade.IRPrint.indexNameMap m
                for KeyValue (cid, cl) in callables do
                    eprintfn "[llvm ir-dump] callable %d (%s):" cid cl.Name
                    eprintfn "%s" (Blade.IRPrint.ppIRExprWithNames names 1 cl.Body)
            let c = newCtx callables (computeModuleFacts m callables)
            // Which bodies take the Int64 -> Float64 split: the C++ lane's set
            // (CodeGenExprSupport, "WHERE `Float64(x)`"), read per body below.
            Blade.CodeGenExprSupport.setI64SplitReach (Blade.CodeGenExprSupport.i64SplitReachOf m)
            let (moduleReads, publishWanted, deferredMaps) = computeModuleReads m callables
            c.ModuleReads <- moduleReads
            c.PublishWanted <- publishWanted
            c.DeferredModule <- deferredMaps
            c.ModuleNames <- m.Bindings |> List.map (fun b -> (b.Id, b.Name)) |> Map.ofList
            // main FIRST: it discovers which functions are actually reachable,
            // and the worklist below emits only those. An unreachable helper
            // (a static function already folded away, say) never has to be
            // supported, which is the difference between refusing a program
            // and refusing a declaration nothing calls.
            let mainLines = Blade.CodeGenExprSupport.withI64SplitScope false (fun () -> emitMain c m programName)
            while c.Pending.Count > 0 do
                let cl = c.Pending.Dequeue()
                let sym = c.Emitted.[cl.Id]
                c.Funcs.Add(Blade.CodeGenExprSupport.withI64SplitScope (Blade.CodeGenExprSupport.i64SplitReaches cl.Id) (fun () -> emitFunctionBody c cl sym))
            let mainLines, funcs =
                match resolveFrames c (mainLines :: List.ofSeq c.Funcs) with
                | m :: fs -> m, fs
                | [] -> mainLines, []
            let sb = StringBuilder()
            let put (s: string) = sb.Append(s).Append('\n') |> ignore
            put "; Blade -- generated by the BLADE_LLVM lane (src/EmitLlvm.fs)."
            put "; No target triple: clang supplies the host's. Fast-math flags:"
            put "; `reassoc nsz` ONLY on licensed accumulator chains (off under"
            put "; BLADE_FP_REPRO=1), `contract` per instruction unless"
            put "; BLADE_FP_CONTRACT=off -- the C++ lane's default regime."
            // The omp census (G11): one line per kernel that asked for omp,
            // emitted only when one did, so a program without a licence keeps
            // its exact text.
            for k in ompRequestedKernels program do
                put $"; {(ompCensusPhrase ())} ({k})"
            put ""
            for g in c.Globals do put g
            if c.Globals.Count > 0 then put ""
            for d in c.Externs do put d
            put ""
            for f in funcs do
                for line in f do put line
                put ""
            for line in mainLines do put line
            // Attribute groups last, as clang writes them, and only the ones
            // something referenced.
            if c.UsedAttrGroups.Count > 0 then
                put ""
                // Any declared shim symbol that can panic (shimPanicSymbols: the
                // panic path and the arithmetic-contract helpers).
                let anyPanics =
                    c.Externs |> Seq.exists (fun d ->
                        shimPanicSymbols |> Set.exists (fun sym -> d.Contains("@" + sym + "(")))
                for g in c.UsedAttrGroups do
                    put ($$"""attributes #{{g}} = { {{(attrGroupText c.AnyFrees anyPanics g)}} }""")
            Ok (sb.ToString())
            finally Blade.IR.restoreAnalysisContext prevCtx
    // A refused program emitted nothing: its BL7005 warnings are discarded
    // (the C++ lane it falls back to raises its own).
    with LlvmRefusal reason -> Blade.Diagnostics.EmitterWarnings.reset (); Error reason

/// The fixed back-end surface (`IRProgram -> Result<string, string>`).
/// Programs reaching this entry are unnamed; the timing line reads "program".
let tryEmitProgram (program: IRProgram) : Result<string, string> =
    tryEmitProgramNamed "program" program

// ---------------------------------------------------------------------------
// Runtime shim deployment
// ---------------------------------------------------------------------------

/// The C runtime the emitted `.ll` links against. Deployed beside the `.ll`
/// exactly as `deployRuntimeHeaders` deploys the C++ headers -- shipped in
/// src/cpp/, copied into the build output by Blade.fsproj, read from disk at
/// emit time.
let shimFileName = "blade_llvm_shim.c"

/// The headers the shim `#include`s from its own directory: deployed beside
/// it (`deployShim`) and hashed into its object identity (Build.buildLlvmShim).
/// blade_member.h is membership hoisting's C core, shared with the C++ lane.
let shimIncludedHeaders = [ "blade_member.h" ]

let shimSourceText () : string =
    Blade.CodeGenLoopNest.readCppRuntimeHeader shimFileName

/// Write the shim next to a generated `.ll`, skipping the write when the
/// destination already holds byte-identical content (same rule, and the same
/// hand-edit-preserving rationale, as deployRuntimeHeaders).
let deployShim (outputDir: string) : unit =
    let dest = System.IO.Path.Combine(outputDir, shimFileName)
    let text = shimSourceText ()
    let current =
        try (if System.IO.File.Exists dest then Some (System.IO.File.ReadAllText dest) else None)
        with _ -> None
    if current <> Some text then
        System.IO.File.WriteAllText(dest, text)
    // The tile core (M6) and the arithmetic header it shares with the C++
    // lane ride along: Build.buildLlvmTilesObject compiles them only for a
    // `.ll` that calls the core, so deploying them is a write, not a compile.
    // The headers the shim itself includes (shimIncludedHeaders: the
    // membership core) are deployed the same way.
    for name in [ tilesSourceFileName; tilesHeaderFileName ] @ shimIncludedHeaders do
        let dest = System.IO.Path.Combine(outputDir, name)
        let text = Blade.CodeGenLoopNest.readCppRuntimeHeader name
        let current =
            try (if System.IO.File.Exists dest then Some (System.IO.File.ReadAllText dest) else None)
            with _ -> None
        if current <> Some text then
            System.IO.File.WriteAllText(dest, text)
