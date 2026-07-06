module Blade.Tests.Interfaces

let test51_interfaceDecl = """
// Interface declaration
interface Measurable {
    function area(self) -> Float64
}
struct Circle {
    radius: Float64
}
"""

let test52_implDecl = """
// Interface implementation
interface Scalable {
    function scale(self, factor: Float64) -> Float64
}
struct Box {
    width: Float64,
    height: Float64
}
impl Scalable for Box {
    function scale(self, factor: Float64) -> Float64 = self.width * self.height * factor
}
"""

let test55_implMethodCall = """
// Interface + impl + method call with verified result
struct Rect {
    w: Float64,
    h: Float64
}
interface HasArea {
    function area(self) -> Float64
}
impl HasArea for Rect {
    function area(self) -> Float64 = self.w * self.h
}
let r = Rect { w = 3.0, h = 4.0 }
let a = r.area()
// EXPECT: a = 12
"""

let test56_implMultipleMethods = """
// Multiple methods in one impl block
struct Vec2 {
    x: Float64,
    y: Float64
}
interface VecOps {
    function dot(self, other: Vec2) -> Float64
    function scale(self, s: Float64) -> Vec2
}
impl VecOps for Vec2 {
    function dot(self, other: Vec2) -> Float64 = self.x * other.x + self.y * other.y
    function scale(self, s: Float64) -> Vec2 = Vec2 { x = self.x * s, y = self.y * s }
}
let v1 = Vec2 { x = 3.0, y = 4.0 }
let v2 = Vec2 { x = 1.0, y = 2.0 }
let d = v1.dot(v2)
let v3 = v1.scale(2.0)
let sx = v3.x
let sy = v3.y
// EXPECT: d = 11
// EXPECT: sx = 6
// EXPECT: sy = 8
"""

/// Interface and impl tests
let interfaceTests = [
    ("Interface Declaration", test51_interfaceDecl)
    ("Impl Declaration", test52_implDecl)
    ("Impl Method Call", test55_implMethodCall)
    ("Impl Multiple Methods", test56_implMultipleMethods)
]
