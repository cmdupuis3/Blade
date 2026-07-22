// Run deep-recursion-prone work on a thread with a large stack.
//
// The compile pipeline walks the AST/IR by mutual recursion — one native
// frame per nesting level (TypeCheck.inferExpr/inferBinOp, CodeGen.exprToCpp,
// Lowering, IR validation, ...). The ppl jet elaborator (dist_jet/dist_map)
// generates arithmetic chains ~150+ operators deep, and those blow the default
// ~1 MB thread stack. Debug builds overflow first (no tail-call/frame-size
// optimizations); Release only has more headroom, so a deep enough program
// would overflow there too. Repro: tests/corpus/ppl/{033,034,036,039}.blade,
// which stack-overflow under a Debug `blade check` and are fine on a big stack.
//
// The fix, standard for recursive-descent compilers (the F# compiler itself
// does this), is to run the pipeline on a dedicated large-stack thread. The
// two chokepoints wrapped are Cli.dispatch (all CLI commands) and the test
// runner's per-test F# pipeline.
module Blade.Runtime

open System.Threading
open System.Runtime.ExceptionServices

/// Reserved stack for compile-pipeline worker threads: 64 MB, ~60x the
/// observed worst case (≈316 frames / ~1 MB for the deepest elaborated chain).
/// This is a RESERVATION only — pages commit on demand as recursion touches
/// them — so the cost is nil until a program actually recurses that deep.
let largeStackBytes = 64 * 1024 * 1024

/// Run `work` on a dedicated thread with a large stack and return its result.
/// Any exception is re-raised on the caller's thread with its original stack
/// trace preserved (via ExceptionDispatchInfo), so callers that pattern-match
/// on thrown exceptions see identical behavior to a direct call.
let runOnLargeStack (work: unit -> 'T) : 'T =
    let mutable result = Unchecked.defaultof<'T>
    let mutable captured : ExceptionDispatchInfo = null
    let body () =
        try result <- work ()
        with ex -> captured <- ExceptionDispatchInfo.Capture ex
    let t = Thread(ThreadStart body, largeStackBytes)
    t.Start()
    t.Join()
    match captured with
    | null -> result
    | edi -> edi.Throw(); Unchecked.defaultof<'T>   // Throw() always throws; line unreachable
