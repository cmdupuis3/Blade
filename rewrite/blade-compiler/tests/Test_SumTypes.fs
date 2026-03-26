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

/// Sum type tests
let sumTypeTests = [
    ("Sum Type Simple", test48_sumTypeSimple)
    ("Sum Type With Data", test49_sumTypeWithData)
    ("Sum Type Match", test50_sumTypeMatch)
    ("Sum Type None", test50b_sumTypeNone)
    ("Sum Type Nested", test50c_sumTypeNested)
]
