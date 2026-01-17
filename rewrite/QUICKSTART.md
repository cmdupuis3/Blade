# Blade: Quickstart Guide

Blade is an array-oriented functional programming language, designed for scientific applications. Blade is useful for general-purpose array math, and particularly for math involving symmetric calculations.

Blade provides transparent access to optimal coding patterns. Blade guarantees that array computations are cache-optimal at the type level. Blade also uses type deduction to encode data symmetry and function commutativity, which together can yield extreme accelerations for certain kinds of problems.

Blade can look and behave very similarly to languages like Python and R, and packages like numpy and xarray. However, Blade is built on a radically different foundation that provides access to powerful abstractions.

The three most important concepts in Blade (beyond basic programming) are:

1. **Loop Reification**: This means Blade treats iteration patterns (i.e., for-loops) as first-objects. In Blade, you can store an unevaluated for-loop in a variable, join it with other for-loops, and apply it to multiple different computations.
2. **Dimensional Currying**: Arrays in Blade are not simply containers of data; arrays are functions that take indices and evaluate to their elements. Dimensional currying may seem strange, but it makes complex array computations easier to reason about in terms of how they are shaped.
3. **Arity Polymorphism**: In Blade, you can write a function that takes an arbitrary number of input arrays, but still deduces the correct shape and type of the output from the inputs.

Together, these concepts form the core of what we can call "structure-first" programming, as opposed to "collection-first," which is how most languages are oriented.

## 1. Basic Concepts

Blade's surface-level math syntax should look and feel very familiar to those programming in Python's numpy, R, MATLAB, and similar.

ML-style syntax forms most of the rest of Blade's basic grammar. This should look familiar to anyone using Rust, OCaml, F#, etc.

Putting the two together, basic assignment looks like

```F#
let a = 2
let b = 3
let mySum = a + b

mySum
```
```
5
```

For conditionals, we have `if`/`then`/`else` and pattern matching with `match` statements. Both are first-class functions and the results can be let-bound to values.

```F#
let test1 = if a == 2 then True else False
test1
```
```
True
```

Match statements are much more flexible. They can serve as switch statements.

```F#
let test2 = 
    match a with
    | 1 -> True
    | 2 -> False
    | 3 -> False
    | _ -> True
```
```
False
```

## 2. Array and Index Types

In Blade, the main way to create a collection-type is with arrays. Arrays in Blade have types that tell you exactly how the array is structured. We use "Index types" to define the multidimensional space that the array spans. 

An simple index type functions somewhat like `range` does in Python. It just says, "I have this many indices"...

```F#
Idx<N>
```

An index type is a type in Blade. We define types in Blade with the `type` keyword. 

```F#
type MyType = Idx<100>
```

This says, "there are 100 elements." Index types don't evaluate to values, you cannot see the exact numbers inside, but they guarantee that the `Array` types they help define will iterate in the fastest order.

An `Array` is, obviously, a type for arrays. `Array` types require a value type (like `Float` or `Int`) and one or more index types.


```F#
type LatIdx = Idx<180>
type LonIdx = Idx<360>

type EarthArray = Array<Float like LatIdx, LonIdx>
```

We can use `Array` types to define arrays. 

```F#
let array: EarthArray = readArray("file_with_array_inside.nc")
```

What did we just assign to `array`? We don't know exactly from this code, but we *can* say that `array` is an `EarthArray`, and we know what shape it's supposed to have.

## 3. Function Basics

In Blade, we have two parallel type systems, *abstract types* and *concrete types*. `EarthArray` is a concrete type. We know because we defined it ourselves, there's no mystery about what it means. It's a 180 x 360 `Array` of `Float` values.

But functions in Blade don't see all these details. They're on a need-to-know basis. They only know about abstract types.

Let's compare; `Array` objects have types like this:
```F#
type EarthArray = Array<Float like LatIdx, LonIdx>
```
Functions see only a part of the information. To a function, `EarthArray` looks like:
```F#
Float^2(1,2)
```

What does this mean?

Abstract types come in the form

```F#
T^rank(symm)
```

Where `T` denotes an arbitrary value type, `rank` is the rank (or dimensionality) of the array, and `symm` is a vector of length `rank` that tracks symmetry between dimensions. We'll ignore symmetry for now.

So now what? We have an `Array`, but we want to do something with it.

We can define a function with `function`:

```F#
function add1(array: T^2) -> T^2 =
   array + 1
```

This function takes a 2D array, adds one to it, and returns the result, which is a 2D array.

The type signature of `add` is

```
T^2 -> T^2
```

...which means that add1 is a function takes a 2D array and returns a 2D array. 

Function scopes in Blade work intuitively: a variable defined outside the scope of the function can't be modified inside the function, unless you tell the function that's allowed with `mut`, for "mutable."

```F#
mut a = 2
function tryToChange(a: T^0) = 
    a + 10
a
```
```
12
```

The opposite is `static`, which tells the compiler that this value can never change.

```F#
static a = 2
function tryToChange(a: T^0) = 
    a + 10
a
```
```
Error!
```

The middle case is regular `let`, which allows a to change in the scope it lives in, but functions will error if they try to modify it.

```F#
let a = 2
function tryToChange(a: T^0) = 
    a + 10
a
```
```
Error!
```
(but this is okay)
```F#
let a = 2
function tryToChange(a: T^0) = 
    a + 10
let a = tryToChange a
a
```
```
12
```

We also have a way to write short functions when we don't need to name them. Anonymoous functions are called "lambda expressions." Using `lambda`:

```F#
function myFunc = lambda(x: Float) -> x + 10
```

Notice that we didn't have to annotate the type on myFunc; the compiler figures it out based on what's in the lambda expression. 

## 4. Structure-First Math

When it comes to doing math with arrays, Blade does things a little differently.

Let's say we want to know the average global temperature. We get our temperature data:

```F#
let temps = getTemperatures("temperatures.nc")
```
`temps` now has a concrete type:
```
Array<Float like Idx<100>, Idx<120>>
```

We want a function that can add all these numbers. That's pretty easy; in fact, we already have one!

```F#
(+)
```

The primitive binop `+` is a function with the type signature

```
T^0 -> T^0 -> T^0
```

However, there is more going on.

Blade wraps all the primitive binops (of type `T^0 -> T^0 -> T^0`) in for-loops. How many? That depends on the arrays.

In numpy, R, etc, `+` adds arrays elementwise. In one sense, that's intuitive.

But, Blade functions don't add two arrays elementwise just because. Blade functions look at *all* the dimensions it sees before they start iterating. There are two dimensions from A and two from B. Blade sees that the primitive `+` expects two T^0 elements, and it intends to keep it that way.

Let's build our own constrained wrapper for primitive `+`:

```F#
let add(a: T^0, b: T^0) -> T^0 =
    a + b
```

This works the same way we usually expect it to. There's no iteration here to confuse us.

What happens when we add a 2D array to a scalar?

```F#
let mySum = add(A, 10)
```

Blade will see that the second argument is a single element. Yay! It matches the type signature of `+`!

But `A` does not >:\(

Then... we'll *make* it fit. Blade constructs two for-loops over `A`. That gets us down to `T^0`. Blade is now happy. 

Two for-loops is also what you might do in (naive) Python, C, Fortran, etc. But then...

If we have...

```F#
let mySum = add(A, B)
```

Blade is unhappy again. There is a 2D array where there should be a `T^0` element. So then, we iterate over them too!

---

The main idea here is that Blade will iterate over *all* the dimensions in a function call where the ranks of the inputs don't match the ranks of the function type signature. That means that the type signature of `+` in

```F#
A + B
```
is
```
T^2 -> T^2 -> T^4
```

That means that this expression is actually adding every element of `A` to every element of `B`. 

Not what you'd expect with numpy.

Instead, we can express elementwise addition like this:

```F#
let add((a: T^0, b: T^0)) = a + b
method_for(zip(A, B)) <@> add
```

There's a lot happening here, but the key fact is that `zip` turns a tuple of arrays into an array of tuples. The extra parentheses inside `add` turn the individual arguments into a single tuple.

So, whereas before, Blade saw four dimensions and decided to iterate over all of them, `zip` reduced the rank of the array that Blade sees before it tries to iterate.

So what could possibly be the benefit of thinking this way?

(...stay tuned)


