# Blade: Quickstart Guide

Blade is an array-oriented functional programming language, designed for scientific applications. Blade is useful for general-purpose array math, and particularly for math involving symmetric calculations.

Transparent access to optimal coding patterns is a core concern. The type system of Blade guarantees that array computations are cache-optimal, and Blade also uses type deduction to encode data symmetry and function commutativity, which together can yield extreme accelerations for certain kinds of problems.

Blade can look and behave very similarly to languages like Python and R, and packages like numpy and xarray. However, Blade is built on a radically different foundation that provides access to powerful abstractions.

The three most important concepts in Blade (beyond basic programming) are:

1. **Loop Reification**: This means Blade treats iteration patterns (i.e., for-loops) as first-class objects. In Blade, you can store a partially evaluated for-loop in a variable, join it with other for-loops, and apply it to multiple different computations.
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
let test2(a: Nat) = {
    match a with
    | 1 -> True
    | 2 -> False
    | 3 -> False
    | _ -> True
}
test2(2)
```
```
False
```

## 2. Array and Index Types

In Blade, the main way to create a collection-type is with arrays. Arrays in Blade have types that tell you exactly how the array is structured. We use "index types" to define the multidimensional space that the array spans. 

A simple index type functions somewhat like `range` does in Python. It just says, "I have this many indices"...

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

## 3. Currying and Tuples

The other main collection type in Blade is the tuple. Tuples are constructed with a comma-separated series in parentheses:

```F#
let myTuple = (a, b, c)
```

A tuple is a single value; you can have tuples of tuples, tuples of arrays, etc. To get the contents of a tuple, you can easily destructure it in assignment, like:

```F#
let e, d, f = myTuple
```

Tuples in Blade can be partially destructured. Blade will separate out the first elements and return the rest of the tuple as a tuple.

```F#
let head :: tail = (a, b, c)
tail == (b, c)
```
```
True
```

The idea of destructuring a collection one element at a time is called "currying". It also applies to function arguments and array indexing. Indexing into Blade can be done in a curried form, or an uncurried form.

```F#
let view1 = A(i)(j)(k) // curried
let view2 = A(i, j, k) // uncurried
```

Currying has implications for type signatures; a curried function is a function where one argument is passed at a time. More practically, as long as we pass arguments to the function in the correct order, it doesn't matter when we pass the arguments.

```F#
function myFunc(a:T^0, b: T^0, c: U^0, d: U^0) -> T^0 = ...
```
```
T^0 -> T^0 -> U^0 -> U^0 -> T^0
```
```F#
function myFuncA = myFunc(5)
```
```
T^0 -> U^0 -> U^0 -> T^0
```
```F#
function myFuncAB = myFuncA(3)
```
```
U^0 -> U^0 -> T^0
```

...and so on.

With array indexing, currying generally means that you have to index *in order*. The only way to index into a different dimension than the first is to transpose the array. Transposition can be expensive in some cases, but it is the price we pay for dimensional currying, which is what guarantees cache-optimal iteration.


## 4. Function Basics

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
function add1(array: T^2) -> T^2 = {
   array + 1
}
```

This function takes a 2D array, adds one to it, and returns the result, which is a 2D array.

The type signature of `add` is

```
T^2 -> T^2
```

...which means that add1 is a function takes a 2D array and returns a 2D array. 

Function scopes in Blade work intuitively: a variable defined outside the scope of the function can't be modified inside the function, unless you tell the function that's allowed with `mut`, for "mutable."

```F#
let mut a = 2
function tryToChange(a: mut T^0) = {
    a = a + 10
}
tryToChange(a)
a
```
```
12
```

The opposite is `const`, which tells the compiler that this value can never change. This means that reassigning the variable in its scope will error.

```F#
let const a = 2
function tryToChange(a: T^0) = {
    a + 10
}
a = tryToChange(a)
a
```
```
Error!
```

The middle case is regular `let`, which allows a to change in the scope it lives in, but functions will error if they try to modify it.

```F#
let a = 2
function tryToChange(a: T^0) = {
    a = a + 10
}
tryToChange(a)
a
```
```
Error!
```
(but this is okay)
```F#
let a = 2
function tryToChange(a: T^0) = {
    a + 10
}
a = tryToChange(a)
a
```
```
12
```

We also have a way to write short functions when we don't need to name them. Anonymous functions are called "lambda expressions." Using `lambda`:

```F#
function myFunc = lambda(x: Float) -> x + 10
```

Notice that we didn't have to annotate the type on myFunc; the compiler figures it out based on what's in the lambda expression. 

## 5. Structure-First Math

When it comes to doing math with arrays, Blade does things a little differently.

Let's say we want to know the average global temperature. We get our temperature data:

```F#
let temps = getTemperatures("temperatures.nc")
```
`temps` now has a concrete type:
```
Array<Float like Idx<100>, Idx<120>>
```

We want a function that can add all these numbers. That's pretty easy; in fact, we already have one! The primitive binop `+` is a function with the type signature

```
T^0 -> T^0 -> T^0
```

However, there is more going on.

Blade wraps all the primitive binops (of type `T^0 -> T^0 -> T^0`) in for-loops. How many? That depends on the arrays.

In numpy, R, etc, `+` adds arrays elementwise. In one sense, that's intuitive.

But, Blade functions don't add two arrays elementwise just because. Blade functions look at *all* the dimensions they see before they start iterating. There are two dimensions from A and two from B. Blade sees that the primitive `+` expects two T^0 elements, and it intends to keep it that way.

Let's build our own constrained wrapper for primitive `+`:

```F#
let add(a: T^0, b: T^0) -> T^0 = {
    a + b
}
```

This works the same way we usually expect it to. There's no iteration here to confuse us.

What happens when we add a 2D array to a scalar?

```F#
let mySum = add(A, 10)
```

Blade will see that the second argument is a single element. It matches the type signature of `+`! But `A` does not. Then... we'll *make* it fit. Blade constructs two for-loops over `A`. That iterates `A` down to `T^0`. 

Two for-loops is also what you might do in (naive) Python, C, Fortran, etc. But then, if we have...

```F#
let mySum = add(A, B)
```

There is another 2D array `B` where there should be a `T^0` element. So then, we iterate over `B`'s dimensions too!

---

The main idea here is that Blade will iterate over *all* the dimensions individually in a function call where the ranks of the inputs don't match the ranks of the function type signature. That means that the type signature of `+` in

```F#
A + B
```
would be
```
T^2 -> T^2 -> T^4
```

That means that this expression is actually adding every element of `A` to every element of `B`. 

Not what you'd expect with numpy.

Likewise, even though this is the Blade way to add two tensors, we designate it as `[+]`, and let `+` and `(+)` be used for the elementwise operations we find in most other languages.

We can express elementwise addition like this:

```F#
let add((a: T^0, b: T^0)) = a + b
method_for(zip(A, B)) <@> add // A + B
```

while the outer-product-style addition is a little simpler.

```F#
let add(a: T^0, b: T^0) = a + b
method_for(A, B) <@> add // A [+] B
```

There's a lot happening here, but the important thing is that whereas before, Blade saw four dimensions and decided to iterate over all of them separately, `zip` reduced the rank of the array that Blade sees before it tries to iterate.

So what could possibly be the benefit of bracketed operators like `[+]`?

## 6. Comoments: The Motivating Problem

Consider the covariance function:

$$cov(x, y) = \frac{1}{n}\Sigma_n (x_n - \mu_x)(y_n - \mu_y)$$

If we think of a 2D array $A(x, t)$, we can see that the covariance function consumes one dimension due to the sum, and we can reimagine $A$ a 1D array of time series.

If we want a covariance matrix of $A$, we need to know the covariance for every pair of time series. 

You might be tempted to calculate $\forall n: cov(A_n, A_n)$, but this doesn't calculate all the elements we need... it only calculates a diagonal!

What we need is $\forall m,n: cov(A_m, A_n)$. This is very close to an outer-product, i.e., `[*]`.

Covariance is a commutative function, because $cov(x, y) = cov(y, x).$ This means that considering every combination of two time series in $A$, only about half of them are actually unique.

Something interesting happens when we start talking about higher-rank tensors. Consider 2D+time array $B(x, y, t)$. We can iterate through one loop at a time...

```python
for x1 in range(0, xmax):         # Iterate and slice Bm
    for y1 in range(0, ymax):
        for x2 in range(0, xmax): # Iterate and slice Bn
            for y2 in range(0, ymax): 
                cov(B[x1, y1], B[x2, y2])
```

Notice that we are iterating through *two* copies of `x` and *two* copies of `y`. 

Before, we said that $cov(x, y) = cov(y, x)$. But now we have more we can do. We can say that 

$$cov(B(x_1, y_1), B(x_2, y_2)) = cov(B(x_2, y_2), B(x_1, y_1))$$
$$=cov(B(x_1, y_2), B(x_2, y_1)) = cov(B(x_2, y_1), B(x_1, y_2))$$

This is true because $x_1 = x_2$ and $y_1 = y_2$. This situation creates what we can call *product symmetry*. Product symmetry occurs when a commutative function interacts with multiple copies of the same, 2D+ array. 

In the 1D+time case, we don't really notice this because

$$cov(A(x_1), A(x_2)) = cov(A(x_2), A(x_1))$$

...yields the same number of unique combinations as just commuting $A$. Simple commutativity can be thought of as a diagonal subset of product symmetry, and we don't notice it with 1D arrays because the diagonal susbet of unique dimension equivalences is the whole matrix; there's only one element.

However, in 2D, product symmetry implies that for 2D+time array $B$, the number of unique covariance elements is $1/4$, not $1/2$.

Why is this important? Because in a typical workflow, if we want to get a covariance matrix, we think "flatten each array of time series into 1D+time and calculate covariance of each pair of points."

If we want optimal speed, *THIS IS WRONG!*

Flattening the dimensions of $B$ actually destroys critical information that helps us find which covariance matrix elements are truly unique.

Consider how we would iterate over $A$ to calculate only unique combinations:

```python
for x1 range(0, xmax):
    for x2 range(x1, xmax):
        cov(A[x1], A[x2])
```

This is triangular iteration. Triangular iteration over two loops will visit only about half of the total iteration space. Revisiting the $B$ situation, let's rearrange and add triangular bounds...

```python
for x1 in range(0, xmax):
    for x2 in range(x1, xmax): 
        for y1 in range(0, ymax):
            for y2 in range(y1, ymax): 
                cov(B[x1, y1], B[x2, y2])
```

This exactly the iteration pattern for product symmetry, and we can see clearly how we would only have $1/4$ unique elements. 

---

Consider now another calculation: coskew over 2D+time array $C$.

$$coskew(x, y, z)= \frac{1}{n} \Sigma_n (x_n - \mu_x)(y_n - \mu_y)(z_n - \mu_z)$$

The unique calculations would have nested triangular iteration like this:

```python
for x1 in range(0, xmax):
    for x2 in range(x1, xmax): 
        for x3 in range(x2, xmax):
            for y1 in range(0, ymax):
                for y2 in range(y1, ymax): 
                    for y3 in range(y2, ymax):
                        coskew(C[x1, y1], C[x2, y2], C[x3, y3])
```

Each triangular block iterates over only $1/6$ of the full iteration space it covers.  Since we have two blocks of $1/6$, we see that a total of $1/36$ of the volume of the total iteration space is unique.

These could be pretty major speedups, if only we could exploit them easily...

## 7. Symmetry Optimization

In Blade, we know that the fastest way is the *only* way to iterate over an array. That includes low-level caching behavior, as well as exploiting symmetry like we just discussed in Section 5.

But in Blade, we define Arrays based on index types, so how can we have triangular iteration in an array like this?

```F#
let array = Array<Float like Idx<100>, Idx<100>>
```

Well... we can't! We need a new index type that describes *multiple* symmetric indices simultaneously, because as we saw before, the bounds now depend on each other sequentially. So, if we want a symmetric array, we need something new: a symmetric index type.

```F#
SymIdx<r, N>
```

`SymIdx` doesn't just know the extents, it also knows how many different copies of this dimension it needs to nest triangularly for optimal speed. While `Idx`'s abstract type  is `T^1`, `SymIdx` has abstract type `T^r`.

```F#
type SymArray = Array<Float like SymIdx<2, 100>>
```

A `SymArray` only takes up *half* as much memory as `array`, and it also encodes how to iterate over that symmetric space.

Thinking back to our covariance calculation $cov(B, B)$, let's make it a little more concrete. We can have

```F#
type Btype = Array<Float like Idx<100>, Idx<200>>
let B: Btype = ...
let calc = cov(B, B)
```

...for a function `cov`. We know that `cov` is commutative, and it's going to give us a `T^4` array when it completes. The concrete type signature of `calc` is therefore:

```
Array<Float like SymIdx<2, 100>, SymIdx<2, 200>>
```

Each symmetric index type contributes two copies of each dimension, for a total of four. It also encodes product-symmetric iteration patterns.

## 8. Loop Objects

We have established that product symmetry is important for guaranteeing optimal speed for comoment statistics like covariance and coskeweness. However...

```python
for x1 in range(0, xmax):
    for x2 in range(x1, xmax): 
        for x3 in range(x2, xmax):
            for y1 in range(0, ymax):
                for y2 in range(y1, ymax): 
                    for y3 in range(y2, ymax):
                        coskew(C[x1, y1], C[x2, y2], C[x3, y3])
```

...this is a mess. Let's clean it up.

Blade has two ways we can condense this: `object_for` and `method_for`. These are loop combinators; they nest different kinds of for-loops together based on the functions and arrays they bind to.

`object_for` wraps a function in a loop nest, and awaits arrays.

`method_for` wraps arrays in a loop nest, and awaits a function.


Both return "loop objects" which can be stored as a first-class value. `object_for` loops apply to objects. `method_for` loops apply to methods.

```F#
let objectLoop = object_for(func)
let methodLoop = method_for(A, B, C)
```

A loop object is kind of a strange thing. It is a nest of loops... but we don't know exactly how many or what kind they are. We only have some of the information we need, not all of it, to fully specify the iteration pattern.

To finish a computation, we need `<@>`, a special function we can call the "apply" combinator.

```F#
let result1 = {
    object_for(func) 
    <@> (A, B, C) 
    |> compute
}

let result2 = {
    method_for(A, B, C) 
    <@> func 
    |> compute
}

result1 == result2
```
```
True
```

The point of a loop object is that we can reuse an incomplete iteration pattern. Consider:

```F#
let covLoop = object_for(cov)

let covA = covLoop <@> (A, A)
let covB = covLoop <@> (B, B)
let covC = covLoop <@> (C, C)
```

Now it should start making sense why we might want to have a loop object with an undetermined amount of for-loops inside. If `A`, `B`, and `C` all have different ranks, we would have to write three completely different functions because they each require different numbers of for-loops with different kinds of symmetry!

We can also build collections of computations on one array.

```F#
let LoopA = method_for(A)

let avg = LoopA <@> mean
let var = LoopA <@> variance
let q95 = LoopA <@> quantile(95)
```

Another thing: Blade is typed strongly enough that despite the fact that the inputs to covLoop have different ranks, we *still* get the correct types for output arrays:

```F#
A: Array<T like SymIdx<2, M>>
B: Array<T like SymIdx<2, M>, SymIdx<2, N>>
C: Array<T like SymIdx<2, M>, SymIdx<2, N>, SymIdx<2, P>>
```

Most array languages and packages would have a hard time with this.

They would not be able to track symmetry at a type level, or be able to deduce symmetry from the inputs. That means a user would *manually* have to allocate a symmetric tensor and *know* what product symmetry even is to even attempt to do it optimally.

The `object_for` and `method_for` loop combinators just do it.

## 9. Kernel Functions

So far, we've seen how basic functions are declared, and that commutativity and product symmetry happen at the function level. How do these two ideas connect?

We need a way to declare functions such that the compiler knows commutativity is available to be optimized on. Functions can have `where` clauses, which is a place to add more information about the function that's otherwise non-obvious.

```F#
function covariance(a: T^1, b: T^1) 
where comm(a, b) = {
    (a - mean(a)) * (b - mean(b))
}
```

Here, we annotate that commutativity applies for arguments `a` and `b` by using the `where comm()` clause. If two copies of the same argument are passed to `covariance`, we know we have product symmetry. How much depends on the arrays, but in the case of multiple copies of the same array, we take the maximum. The result type will be also be deduced: commutativity optimization results in symmetric output tensors.

When a loop combinator is completed with `<@>`, the nested loop structure is determined by comparing the ranks of input arrays to the ranks of kernels.

```F#
type Array3D = Array<Float like Idx<M>, Idx<N>, Idx<P>>
type Array2D = Array<Float like Idx<M>, Idx<N>>

let array1: Array3D = ...
let array2: Array2D = ...

function kernel1(a: T^1, b: T^1)
where comm(a, b) = {
    a*b - (a+b)
}

function kernel2(c: T^1, d: T^0) = {
    (c - d) / (max(c) - d)
}
```

Looking at the type signatures of results:
```F#
let k1Forward = kernel1(array1, array2)
let k1Reverse = kernel1(array2, array1)
let k2Forward = kernel2(array1, array2)
let k2Reverse = kernel2(array2, array1)
```
```
k1Forward: Array<Float like SymIdx<2, M>, Idx<N>>
k1Reverse: // Err! Need to commute (a, b) for max speedup!
k2Forward: Array<Float like SymIdx<2, M>, SymIdx<2, N>>
k2Reverse: Array<Float like SymIdx<2, M>, Idx<N>, Idx<P>>
```

For `k1Reverse`, it won't compile! This is because `comm(a, b)` guarantees that `a` and `b` can be exchanged, but the array arguments are slightly different types, and in that case, we can still get *partial* product symmetry, but the order of arguments becomes important in finding the fastest way to iterate. We can know the best way, but Blade won't optimize this for you, because it would create an output array with unpredictable dimensions.

## 10. Arity Polymorphism

Arity polymorphism is possibly the most powerful feature in Blade. It means that we can define functions that take varying numbers of arrays, iterate over them properly, and return the correct shape and type of output. 

Like at the end of Section 7, many functions in Blade can return different types of arrays depending on the inputs. Most languages would struggle with functions that return multiple types, but arity polymorphism makes well-typed output possible.

```F#
function comoment_prod(A: Poly<T^1>)
where comm(A) -> T^1 = {
    match arity with
    | 0 -> zero // recursion terminator; keyword "zero" means identity
    | _ ->
        let head, tail = A
        (head - mean(head)) * comoment_prod(tail)
}

function comoment_generator(A: Poly<T^1>)
where comm(A) -> T^0 = {
    mean(comoment_prod(A))
}
```

(We annotate `comm` here twice just to be on the safe side, but only the second one is necessary in this specific case.)

This is an arity-polymorphic comoment-generating function; we can call it with any number of arrays and know that we will get a valid and efficiently calculated comoment tensor.

You can declare an arity-polymorphic function using the `Poly<T^_>` format. The `_` denotes a wildcard in the rank position, meaning we could have a tuple of arrays of different or unknown rank. We constrain it here to 1D arrays by providing the rank: `Poly<T^1>`.

```F#
type Array2D = Array<Float like Idx<X>, Idx<T>>
let A: Array2D = ...

let comomentLoop = object_for(comoment_generator)
let covariance = comomentLoop <@> (A, A)
let coskewness = comomentLoop <@> (A, A, A)
let cokurtosis = comomentLoop <@> (A, A, A, A)
```

Types:

```
covariance: Array<Float like SymIdx<2, X>>
coskewness: Array<Float like SymIdx<3, X>>
cokurtosis: Array<Float like SymIdx<4, X>>
```

## 11. Units of Measure

Primitive types in Blade can be annotated with units of measure.

```F#
Unit meters
Unit seconds
Unit velocity = meters / seconds
type Distance = Float<meters>
type Time = Float<seconds>
type Speed = Float<meters / seconds>
```

Blade will check units of measure in computations to ensure correctness. Units of measure ensure that only valid calculations between units are possible.

```F#
let speed1 = 4.0: Speed
let speed2 = 3.0: Speed
let dist = 4.0: Distance
let time = 2.0: Time
let speed3 = dist / time
```
```F#
(speed1 + speed2 + speed3) / 3
```
```
3.0
```
```F#
speed1 + dist
```
```
Error!
```

Units of measure can also incorporate information about their valid ranges with the optional `min` and `max` parameters.

```F#
type Distance = Float<meters, min=0.0>
```

Units of measure can encode physical type constraints at runtime, ensuring that, for example, salinity calculations never go negative. 

