module Blade.Tests.Structs

let test45_structDecl = """
// Basic struct declaration and construction
struct Point {
    x: Float64,
    y: Float64
}
let p = Point { x = 1.0, y = 2.0 }
"""

let test46_structFieldAccess = """
// Struct field access
struct Vector3 {
    x: Float64,
    y: Float64,
    z: Float64
}
let v = Vector3 { x = 1.0, y = 2.0, z = 3.0 }
let sum = v.x + v.y + v.z
// EXPECT: sum = 6
"""

let test47_structPattern = """
// Struct destructuring in pattern
struct Pair {
    first: Int,
    second: Int
}
let p = Pair { first = 10, second = 20 }
let Pair { first, second } = p
let total = first + second
// EXPECT: total = 30
"""

let test48_structConstraintValid = """
// Struct with where constraint - valid construction
struct Balanced {
    a: Int,
    b: Int,
    total: Int
} where a + b == total
let x = Balanced { a = 3, b = 7, total = 10 }
let result = x.total
// EXPECT: result = 10
"""

let test49_structConstraintArith = """
// Struct with arithmetic constraint - valid
struct Ratio {
    num: Float64,
    den: Float64
} where den != 0.0
let r = Ratio { num = 3.14, den = 2.0 }
let half = r.num / r.den
// EXPECT: half = 1.57
"""

let test50_structConstraintInvalid = """
// Struct with where constraint - INVALID construction (should abort)
struct Balanced {
    a: Int,
    b: Int,
    total: Int
} where a + b == total
let x = Balanced { a = 3, b = 7, total = 99 }
"""

let test_phaseD_podStructArray = """
// array of POD struct.
// Struct has only primitive fields. Builds the array via literal,
// reads back a single field for value-check.
struct Particle {
    x: Float64,
    y: Float64
}
let pts: Array<Particle like Idx<3>> = [
    Particle { x = 1.0, y = 2.0 },
    Particle { x = 3.0, y = 4.0 },
    Particle { x = 5.0, y = 6.0 }
]
let second_x = pts[1].x
// EXPECT: second_x = 3
"""

let test_phaseD_podStructArrayPrint = """
// exercises per-field auto-print of POD struct array.
// Validator can't parse [{x: 1, y: 2}, ...] as a float list, so the
// EXPECT line tests a scalar derived from the array. The print codegen
// runs as a side effect — if it fails to compile, the whole test
// regresses at Compile:OK.
struct Particle {
    x: Float64,
    y: Float64
}
let pts: Array<Particle like Idx<2>> = [
    Particle { x = 1.0, y = 2.0 },
    Particle { x = 3.0, y = 4.0 }
]
let sum_x = pts[0].x + pts[1].x
// EXPECT: sum_x = 4
"""

let test_phaseD_rank3Scalar = """
// rank-3 scalar array literal. Pre-generalization, the
// init path emitted only a TODO comment and the array was uninitialized.
// Now uses the recursive walker that handles arbitrary rank.
let cube = [[[1.0, 2.0], [3.0, 4.0]], [[5.0, 6.0], [7.0, 8.0]]]
let v = cube[1][0][1]
// EXPECT: v = 6
// EXPECT: cube = [1, 2, 3, 4, 5, 6, 7, 8]
"""

let test_phaseD_rank3StructArray = """
// rank-3 array of POD struct. Tests the per-element path
// of the generalized walker, plus chained bracket indexing through three
// levels.
struct Particle {
    x: Float64,
    y: Float64
}
let grid: Array<Particle like Idx<2>, Idx<2>, Idx<2>> = [
    [
        [Particle { x = 1.0, y = 2.0 }, Particle { x = 3.0, y = 4.0 }],
        [Particle { x = 5.0, y = 6.0 }, Particle { x = 7.0, y = 8.0 }]
    ],
    [
        [Particle { x = 9.0, y = 10.0 }, Particle { x = 11.0, y = 12.0 }],
        [Particle { x = 13.0, y = 14.0 }, Particle { x = 15.0, y = 16.0 }]
    ]
]
let v = grid[1][0][1].y
// EXPECT: v = 12
"""

let test_phaseD_arrayOfArrays = """
// explicit `Array<Array<T>>` syntax. Without
// type-level normalization at lowering, this would mis-allocate and
// fail to compile (rank/dims mismatch in genArrayLiteral). With the
// fix, it's flattened to the equivalent multi-Idx form and reuses the
// rank-N init path.
let groups: Array<Array<Float64 like Idx<3>> like Idx<2>> = [
    [1.0, 2.0, 3.0],
    [4.0, 5.0, 6.0]
]
let v = groups[1][0]
// EXPECT: v = 4
// EXPECT: groups = [1, 2, 3, 4, 5, 6]
"""

let test_phaseD_structWithArrayField = """
// struct with an array-typed field. Without lifting,
// `samples = [1.0, 2.0, 3.0]` inline as a struct field value would
// fail at codegen — array literals are statement-level constructs
// (need allocation), not inline expressions. The lift pass moves the
// literal to an auto-let binding, then references it by name in the
// struct lit.
struct Trace {
    id: Int,
    samples: Array<Float64 like Idx<3>>
}
let t = Trace { id = 7, samples = [1.0, 2.0, 3.0] }
let id_val = t.id
let s1 = t.samples[1]
// EXPECT: id_val = 7
// EXPECT: s1 = 2
"""

let test_phaseD_inlineArrayLitInFuncArg = """
// inline array literal as a function argument. Tests
// the generalized lift — IRArrayLit appearing in IRApp args goes
// through liftChildren -> liftChild, which now matches isInlineForm.
// Pre-generalization, this would break because exprToCpp has no
// IRArrayLit case (catchall emits BLADE_UNSUPPORTED_IR_NODE_*).
function sumThree(a: Array<Float64 like Idx<3>>) -> Float64 = {
    a(0) + a(1) + a(2)
}
let v = sumThree([10.0, 20.0, 30.0])
// EXPECT: v = 60
"""

let test_phaseD_arrayOfStructsWithArrayField = """
// array of structs, where the
// struct has an array-typed field. Combines the v15-v20 work:
//   - struct array allocation (v15)
//   - bracket disambiguation (v15)
//   - generalized walker (v17)
//   - array-field lifting in struct lits (v20)
//   - generalized lift in nested positions (v21)
struct Trace {
    id: Int,
    samples: Array<Float64 like Idx<2>>
}
let arr: Array<Trace like Idx<2>> = [
    Trace { id = 1, samples = [1.0, 2.0] },
    Trace { id = 2, samples = [3.0, 4.0] }
]
let v0 = arr[0].samples[1]
let v1 = arr[1].id
// EXPECT: v0 = 2
// EXPECT: v1 = 2
"""

let test_phaseD_reduceOverStructArrayField = """
// reduce over a struct's array field.
// This requires:
//   1. Lift hoists `t.samples` to a let-RHS so codegen has a name to attach
//      a companion `_extents` to
//   2. genBinding's IRFieldAccess case (v23b) synthesizes `<name>_extents`
//      from the field's static array type
// Without (1), the reduce would refer to `t.samples` directly with no
// shape; without (2), the binding would lack a sibling extents declaration
// and `<name>_extents[0]` lookups would fail to compile.
struct Trace {
    samples: Array<Float64 like Idx<3>>
}
let t = Trace { samples = [10.0, 20.0, 30.0] }
let total = reduce(t.samples, (+))
// EXPECT: total = 60
"""

let test_phaseE_structWithStringField = """
// Struct with a string field. Verifies several pieces:
//   - "String" name in field type position resolves through TyNamed to
//     IRTScalar ETString (was missing before; resolved alongside Layer 1)
//   - Struct decl renders the field as std::string in the C++ struct
//   - Struct construction emits .name = std::string("Alice") in the
//     designated initializer
//   - Field access p.name produces an std::string r-value usable for
//     equality comparison against a string literal
struct Person {
    name: String,
    age: Int64
}
let alice = Person { name = "Alice", age = 30 }
let bob = Person { name = "Bob", age = 25 }
let aliceMatch = if alice.name == "Alice" then 1 else 0
let bobMatch = if bob.name == "Alice" then 1 else 0
let ageSum = alice.age + bob.age
// EXPECT: aliceMatch = 1
// EXPECT: bobMatch = 0
// EXPECT: ageSum = 55
"""

/// Struct tests
let structTests = [
    ("Struct Declaration", test45_structDecl)
    ("Struct Field Access", test46_structFieldAccess)
    ("Struct Pattern", test47_structPattern)
    ("Struct Constraint Valid", test48_structConstraintValid)
    ("Struct Constraint Arithmetic", test49_structConstraintArith)
    ("Struct Array Basic", test_phaseD_podStructArray)
    ("Struct Array Print", test_phaseD_podStructArrayPrint)
    ("Rank-3 Scalar Array", test_phaseD_rank3Scalar)
    ("Rank-3 Struct Array", test_phaseD_rank3StructArray)
    ("Nested Array Type", test_phaseD_arrayOfArrays)
    ("Struct With Array Field", test_phaseD_structWithArrayField)
    ("Inline Array Literal Arg", test_phaseD_inlineArrayLitInFuncArg)
    ("Struct Array With Array Field", test_phaseD_arrayOfStructsWithArrayField)
    ("Reduce Struct Array Field", test_phaseD_reduceOverStructArrayField)
    ("Struct With String Field", test_phaseE_structWithStringField)
]

/// Tests that should abort at runtime (constraint violation)
let structAbortTests = [
    ("Struct Constraint Invalid", test50_structConstraintInvalid)
]
