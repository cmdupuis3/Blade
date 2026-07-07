# Blade

Blade is a general purpose array-oriented programming language. 

Blade is primarily built to solve array problems with complex grid structures, particularly involving symmetric arrays. 
The syntax extends the classic ML-style grammar with novel concepts like rank and arity polymorphism, iteration patterns as first-class values, and symmetry deduction from kernel annotations.

Spanning the simplest of arithmetic functions to powerful combinators of combinators, Blade guarantees *the **fastest** way is the **only** way*.

## Requirements

Building and running the existing Blade compiler requires support for:
* F# version 7 or higher
* C++ 20 or later
* NetCDF4 (optional) for file I/O

## Current State

Blade is currently in development, and most of the more interesting developments can be found in /rewrite. The current repo structure is a relic of the preceeding Blade-DSL, a C++-based language extension that implements many of the same core concepts.

