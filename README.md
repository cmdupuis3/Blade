# Blade

Blade is a general purpose array-functional programming language. 

Blade is primarily built to solve array problems with complex grid structures, particularly involving symmetric arrays. 
The syntax extends the classic ML-style grammar with cutting-edge concepts like rank and arity polymorphism, iteration patterns as first class values, and symmetry deduction from kernel annotations.

Spanning the simplest of arithmetic functions to powerful combinators of combinators, Blade guarantees *the **fastest** way is the **only** way*.

## IDE Support

A VS Code extension is available [here](https://github.com/cmdupuis3/Blade-REPL). Blade-REPL provides a REPL, tooltips for most objects, and full in-editor type deduction.

## Requirements

Building and running the Blade compiler requires support for:
* F# version 7 or higher
* C++ 20 or later

Optional dependencies include:
* NetCDF4
* Zarr
* MPI
* CUDA

## Current State

Blade is currently in development. The progenitor to Blade, Blade-DSL, is now in `/legacy`.
