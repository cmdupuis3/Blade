module Blade.Tests.SumTypes

let test48_sumTypeSimple = """
// Simple sum type (enum-like)
type Direction = North | South | East | West
let d = North
"""

let test49_sumTypeWithData = """
// Sum type with payload
type Option = Some : Int | None
let x = Some(42)
let y = None
"""

let test50_sumTypeMatch = """
// Pattern matching on sum type
type Result = Ok : Int | Err : String
let r = Ok(100)
let value = match r with
    | Ok(n) -> n
    | Err(msg) -> 0
"""

/// Sum type tests
let sumTypeTests = [
    ("Sum Type Simple", test48_sumTypeSimple)
    ("Sum Type With Data", test49_sumTypeWithData)
    ("Sum Type Match", test50_sumTypeMatch)
]
