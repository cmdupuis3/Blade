// The recursive core's ENTRY POINTS (cleanup round 2, K5): the functions of TypeCheckInfer's inference chain that
// a group moved out of the chain calls back into. The chain builds the record ONCE, as its value `inferEntries`,
// and hands it to the group's functions as their first argument; each function binds the entry points it calls
// under their own names, so its body reads as it did inside the chain. Built once: no closure per call.
// A call through an entry point in TAIL position is BOUND (`match .. with Ok t -> Ok t | Error e -> Error e`):
// an indirect call in tail position is a `.tail` call, which makes the function -- and the continuation methods
// the compiler splits it into -- tail-call critical, so its own hand-offs turn into `.tail` calls through the
// runtime's tail-call helper (the cleanup speed finding of 2026-11-21; the same fix as the buildApply* stages).
module Blade.TypeCheckInferEntries

open Blade.Ast
open Blade.IR
open Blade.IRLoopStructure
open Blade.IRStorage
open Blade.IRLift
open Blade.IRMono
open Blade.IRPrint
open Blade.IRValidate
open Blade.Types
open Blade.TypedAst
open Blade.Unify
open Blade.TypeEnv
open Blade.Zonk
open Blade.TypeCheckIde
open Blade.TypeLower
open Blade.TypeCheckSupport
open Blade.TypedBuilder
open Blade.TypeCheckInferUnits
open Blade.TypeCheckInferApply

/// The inference chain's entry points, as curried function values (see the file comment).
[<NoEquality; NoComparison>]
type InferEntries =
    { CheckExpr: TypeEnv -> IRType -> Expr -> TypeResult<TypedExpr>
      InferExpr: TypeEnv -> Expr -> TypeResult<TypedExpr>
      InferReduceGeneral: TypeEnv -> Expr -> Expr -> Expr option -> int option -> TypeResult<TypedExpr> }
