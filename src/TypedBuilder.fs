// The error-or-continue builder for TypeResult (`typed { ... }`), the type
// checker's replacement for hand-threaded `match r with Error e -> Error e | Ok x -> ...`
// staircases. Opened explicitly by the files that use it (no AutoOpen).
module Blade.TypedBuilder

open Blade.Unify

/// The ERROR-OR-CONTINUE builder for TypeResult: `let! x = r` continues with
/// the value of an `Ok` and answers an `Error` unchanged, `do! r` the same for
/// a `TypeResult<unit>`, `return v` is `Ok v`, `return! r` is `r`. A unit
/// statement between the steps (`if c then record ..`) is sequenced by
/// Combine, which runs the rest only after an `Ok ()`; Delay / Run keep the
/// rest a thunk until then, so a block runs in exactly the order it is written.
/// Every member is inline and every continuation [<InlineIfLambda>], so a
/// `typed { ... }` block compiles to the nested matches the staircase was.
type TypedBuilder() =
    member inline _.Bind(r: TypeResult<'a>, [<InlineIfLambda>] f: 'a -> TypeResult<'b>) : TypeResult<'b> =
        match r with
        | Ok x -> f x
        | Error e -> Error e
    member inline _.Return(x: 'a) : TypeResult<'a> = Ok x
    member inline _.ReturnFrom(r: TypeResult<'a>) : TypeResult<'a> = r
    member inline _.Zero() : TypeResult<unit> = Ok ()
    member inline _.Delay([<InlineIfLambda>] f: unit -> TypeResult<'a>) : unit -> TypeResult<'a> = f
    member inline _.Run([<InlineIfLambda>] f: unit -> TypeResult<'a>) : TypeResult<'a> = f ()
    member inline _.Combine(r: TypeResult<unit>, [<InlineIfLambda>] f: unit -> TypeResult<'a>) : TypeResult<'a> =
        match r with
        | Ok () -> f ()
        | Error e -> Error e

/// `typed { ... }`: see TypedBuilder.
let typed = TypedBuilder()

/// A check that answers the error it found (`Some e`) or none, as a TypeResult
/// step: `do! requireNone (check ...)` stops at the first error.
let inline requireNone (check: TypeError option) : TypeResult<unit> =
    match check with
    | Some e -> Error e
    | None -> Ok ()
