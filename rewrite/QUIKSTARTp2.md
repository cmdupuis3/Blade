# Blade: Quickstart Part 2: Advanced Features


## Virtual Arrays

Consider two 3D arrays `A` and `B`. For some function `func`, we can iterate over the arrays such that we get an outer product iteration pattern.

```F#
function func = ...
let loop = object_for(func)
let result = loop <@> (A, B)
result |> compute
```

This happens seamlessly, but what's happening to the indices?

In Blade, a `method_for` or `object_for` loop automatically constructs the iteration space, and emits the exact indices into the kernel, so that `A` and `B` are indexed correctly at the kernel scope. Usually we don't care what the actual indices are, just that they are used to index into the arrays.

Sometimes, though, the exact indices are useful inside the kernel. However, Blade doesn't allow raw emission of indices into the kernel; instead, if we want the indices, we have to use some kind of array object that constructs the shape of an array without actually evaluating to anything. Slicing it fully just echos the indices it took to get to that value, and since there's no real data there, this kind of array object doesn't actually exist in the compiled code: it just becomes part of the nested loop structure.

This is a "virtual array." The simplest kind of virtual array is `range`:

```F#
range<Idx<M>, Idx<N>>
```

`range` simply emits the all the index tuples in its index space.

```F#
function func1(a: T^0, b: T^0) = a * b
function func2(i: Nat, j: Nat, a: T^2, b: T^2) = a(i, j) * b(i, j) 
let loop1 = object_for(func1)
let loop2 = object_for(func2)
let result1 = loop1 <@> (A, B)
let result2 = loop2 <@> (range<Idx<M>, Idx<N>>, A, B)
(result1 |> compute) == (result2 |> compute)
```
```
True
```

We have effectively recreated how for-loops work in most languages with the second pipeline, by using a `range` virtual array. This particular case isn't very efficient, but it demonstrates that virtual arrays can be useful for complex indexing. Imagine if we needed a stencil operator instead of a simple range. We could have...

```F#
let result2 = loop2 <@> (stencil<Idx<M>, Idx<N>>, A, B)
```

Virtual arrays allow us to map the raw positions in the index space defined by the arrays to a set of indices to be emitted into the kernel. This abstraction means we can imagine different kinds of mappings, with minimal overhead as far as the loop nest is concerned. We still call the same `method_for` and `object_for` loop combinators when we want a loop object.



## For Loops

`method_for` and `object_for` can be unwieldy syntax for many cases, so we can use a short-hand instead.

```F#
let loop1 = for (A, B)
let result1 = loop1 <@> func
let loop2 = for func
let result2 = loop2 <@> (A, B)
```

Let-bound for loops are just syntactic sugar for `method_for` and `object_for`. They function basically the same way, just with less syntax. Going further, we can also use virtual arrays with let-bound for loops.

```F#
let loop = for (A, B) in range<Idx<M>, Idx<N>>
let result = loop <@> lambda(i, j, a, b) -> ...
```

## Zero Functions and Zero Array Tuples


## Parallelism



## Loop Combinators

Blade offers a number of combinators beyond `<@>` to help build performant pipelines. 

The simplest of these are straight from functional programming.
* `|>` The pipe combinator. Pass the value on the left as the last argument to the function on the right.
* `>>` The compose combinator. Given two sequential functions, treat them as one.

A number of combinators are available for loop fusion and composition. 
* `<&>` Fuse loop nest if possible.
* `<&!>` Force complete loop nest fusion; error if it fails.
* `>>@` Compose two kernels that have already been wrapped in `object_for`; `oloop1 >>@ oloop2`
* `@>>` Compose two kernels at `method_for` call sites; `(mloop <@> func1) @>> (mloop <@> func2)`

While Blade guarantees performance at the level of a single kernel and array tuple, these combinators provide more complex options that Blade can't always reason about, and it is up to the user to ensure that pipelines are built performantly.

### Complex Loop Combinators

We can imagine more powerful abstractions with combinators. For an array of functions or computations, we might have

```F#
let pipeline = object_for(>>)
let fuse_all = object_for(<&>)
let first_success = object_for(<|>)
```


## Array Combinators


## Arity Polymorphism Semantics


## Reynolds Operators


## Equivariance Annotations


## Metaprogramming with Static Functions



