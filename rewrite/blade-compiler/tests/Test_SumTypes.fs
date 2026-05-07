module Blade.Tests.SumTypes

// ============================================================================
// Basic Sum Type Construction
// ============================================================================

let test48_sumTypeSimple = """
// Simple sum type (enum-like) — match extracts tag as int
type Direction = North | South | East | West
let d = South
let val = match d with
    | North -> 1
    | South -> 2
    | East -> 3
    | West -> 4
// EXPECT: val = 2
"""

let test49_sumTypeWithData = """
// Sum type with payload — construct and extract via match
type Option = Some : Int | None
let x = Some(42)
let val = match x with
    | Some(n) -> n
    | None -> 0
// EXPECT: val = 42
"""

let test50_sumTypeMatch = """
// Pattern matching on sum type — multiple cases
type Result = Ok : Int | Err : Int
let r1 = Ok(100)
let r2 = Err(404)
let v1 = match r1 with
    | Ok(n) -> n
    | Err(e) -> 0 - e
let v2 = match r2 with
    | Ok(n) -> n
    | Err(e) -> 0 - e
// EXPECT: v1 = 100
// EXPECT: v2 = -404
"""

let test50b_sumTypeNone = """
// Sum type — match on no-payload variant
type Option = Some : Float64 | None
let x = None
let val = match x with
    | Some(n) -> n
    | None -> -1.0
// EXPECT: val = -1
"""

let test50c_sumTypeNested = """
// Nested match — sum type result feeds another computation
type Option = Some : Float64 | None
let a = Some(3.0)
let b = Some(4.0)
let va = match a with
    | Some(n) -> n * n
    | None -> 0.0
let vb = match b with
    | Some(n) -> n * n
    | None -> 0.0
let sum = va + vb
// EXPECT: va = 9
// EXPECT: vb = 16
// EXPECT: sum = 25
"""

let test_phaseD_sumEnumArray = """
// Phase D probe: Array of enum-like sum type (no payload variants).
// Tests construction via ArrayLit, bracket-indexing into the array,
// and match dispatch on the indexed result. Match for enum-style
// variants compiles to `==` comparisons on the underlying enum int.
type Status = Pending | Active | Completed
let states: Array<Status like Idx<3>> = [Pending, Active, Completed]
let v = match states[1] with
    | Pending -> 1
    | Active -> 2
    | Completed -> 3
// EXPECT: v = 2
"""

let test_phaseD_sumPayloadArray = """
// Phase D probe: Array of sum type with data-bearing variants. Match
// dispatch uses std::holds_alternative + std::get<>::value on the
// indexed array element. Tests both Yes(n) extraction and No fallback
// across distinct array elements.
type Maybe = Yes : Int | No
let arr: Array<Maybe like Idx<3>> = [Yes(10), No, Yes(30)]
let v0 = match arr[0] with
    | Yes(n) -> n
    | No -> 0
let v1 = match arr[1] with
    | Yes(n) -> n
    | No -> 0
let v2 = match arr[2] with
    | Yes(n) -> n
    | No -> 0
// EXPECT: v0 = 10
// EXPECT: v1 = 0
// EXPECT: v2 = 30
"""

/// Sum type tests
let sumTypeTests = [
    ("Sum Type Simple", test48_sumTypeSimple)
    ("Sum Type With Data", test49_sumTypeWithData)
    ("Sum Type Match", test50_sumTypeMatch)
    ("Sum Type None", test50b_sumTypeNone)
    ("Sum Type Nested", test50c_sumTypeNested)
    ("Phase D: Sum Enum Array", test_phaseD_sumEnumArray)
    ("Phase D: Sum Payload Array", test_phaseD_sumPayloadArray)
]
