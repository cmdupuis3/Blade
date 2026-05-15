module Blade.Tests.ExprAttrs

// ============================================================================
// Phase B corpus tests for ExprAttrs.
//
// Each test directly constructs an IR fragment with known FreeVars and
// BoundVars, calls exprAttrs, and reports whether the actual result matches
// the expected one. The tests do NOT go through parse/typecheck/lower;
// they are unit tests on the attribute computation only.
//
// The purpose is to validate that exprAttrs correctly handles:
//   - leaves (constants, variables, params)
//   - binders (IRLet, IRLambda, IRForRange, IRMatch with patterns)
//   - capture semantics (lambda body refs to outer-scope IRIds)
//   - shadowing (inner binder of an id that's also free outside)
//   - every inline-form arm and IR construct, to catch arms a future
//     code-change might miss when adding a new variant
// ============================================================================

open Blade.IR

/// A single corpus test: name + a thunk that returns the actual attrs and
/// the expected attrs. The runner compares them and reports per-test.
type AttrsTest = {
    Name: string
    Run: unit -> ExprAttrs * ExprAttrs   // (actual, expected)
}

let private mkAttrs (free: int list) (bound: int list) (isPure: bool) : ExprAttrs =
    { FreeVars  = Set.ofList free
      BoundVars = Set.ofList bound
      IsPure    = isPure
      Probes    = [] }

/// Build an ExprAttrs with explicit probe list. Used by tests that
/// verify probe collection. The probes' Node field is filled with a
/// reference-identity placeholder; tests compare on BuildOn only.
let private mkAttrsWithProbes
        (free: int list) (bound: int list) (isPure: bool)
        (probeArrays: IRExpr list) : ExprAttrs =
    let probes =
        probeArrays |> List.map (fun arr ->
            // Node here is just a placeholder for the test's expected value;
            // attrsEqual compares probes by BuildOn (structural) only.
            { Node = IRLit (IRLitInt 0L); BuildOn = arr })
    { FreeVars  = Set.ofList free
      BoundVars = Set.ofList bound
      IsPure    = isPure
      Probes    = probes }

let private intTy = IRTScalar ETInt64
let private boolTy = IRTScalar ETBool

/// ------------------------------------------------------------
/// 1. Leaves
/// ------------------------------------------------------------

let test_lit_has_no_refs = {
    Name = "Leaf: literal has empty attrs"
    Run = fun () ->
        let actual = exprAttrs (IRLit (IRLitInt 42L))
        let expected = mkAttrs [] [] true
        (actual, expected)
}

let test_var_is_free = {
    Name = "Leaf: IRVar contributes one FreeVar"
    Run = fun () ->
        let actual = exprAttrs (IRVar (7, intTy))
        let expected = mkAttrs [7] [] true
        (actual, expected)
}

let test_param_has_no_refs = {
    Name = "Leaf: IRParam has empty attrs (params are not variables)"
    Run = fun () ->
        let actual = exprAttrs (IRParam ("p", 0, intTy))
        let expected = mkAttrs [] [] true
        (actual, expected)
}

/// ------------------------------------------------------------
/// 2. Simple compositions
/// ------------------------------------------------------------

let test_binop_unions_children = {
    Name = "BinOp: free vars from both operands union"
    Run = fun () ->
        // x + y → free {x, y}
        let e = IRBinOp (IRElementwise, IRAdd, IRVar (1, intTy), IRVar (2, intTy))
        let actual = exprAttrs e
        let expected = mkAttrs [1; 2] [] true
        (actual, expected)
}

let test_unaryop_passes_through = {
    Name = "UnaryOp: free vars pass through"
    Run = fun () ->
        // -x → free {x}
        let e = IRUnaryOp (IRNeg, IRVar (3, intTy))
        let actual = exprAttrs e
        let expected = mkAttrs [3] [] true
        (actual, expected)
}

let test_tuple_unions_all = {
    Name = "Tuple: free vars from all elements union"
    Run = fun () ->
        // (x, y, x) → free {x, y}
        let e = IRTuple [IRVar (1, intTy); IRVar (2, intTy); IRVar (1, intTy)]
        let actual = exprAttrs e
        let expected = mkAttrs [1; 2] [] true
        (actual, expected)
}

/// ------------------------------------------------------------
/// 3. IRLet binder
/// ------------------------------------------------------------

let test_let_binds_id = {
    Name = "Let: binder ID is added to BoundVars, removed from FreeVars"
    Run = fun () ->
        // let x = 1 in x + y
        // FreeVars: {y}; BoundVars: {x}
        let e =
            IRLet (10, IRLit (IRLitInt 1L),
                IRBinOp (IRElementwise, IRAdd, IRVar (10, intTy), IRVar (20, intTy)))
        let actual = exprAttrs e
        let expected = mkAttrs [20] [10] true
        (actual, expected)
}

let test_let_shadowing = {
    Name = "Let: inner let shadows outer free"
    Run = fun () ->
        // y is free; in body, let y = ... rebinds it; the binding shows up
        // in BoundVars, but FreeVars of the inner subexpression that uses
        // the inner y won't surface y as free. The OUTER let binds id 10,
        // and the inner let binds id 20. Both should appear in BoundVars.
        // FreeVars = {z} (referenced after both binders).
        //
        //   let x10 = z in
        //     let y20 = 5 in
        //       x10 + y20
        let inner = IRLet (20, IRLit (IRLitInt 5L),
                        IRBinOp (IRElementwise, IRAdd, IRVar (10, intTy), IRVar (20, intTy)))
        let e = IRLet (10, IRVar (99, intTy), inner)
        let actual = exprAttrs e
        let expected = mkAttrs [99] [10; 20] true
        (actual, expected)
}

let test_let_value_can_be_free = {
    Name = "Let: value's free vars are not bound by the let's own ID"
    Run = fun () ->
        // let x = y in x + 1
        // FreeVars: {y}; BoundVars: {x}
        let e =
            IRLet (10, IRVar (5, intTy),
                IRBinOp (IRElementwise, IRAdd, IRVar (10, intTy), IRLit (IRLitInt 1L)))
        let actual = exprAttrs e
        let expected = mkAttrs [5] [10] true
        (actual, expected)
}

/// ------------------------------------------------------------
/// 4. IRLambda binder
/// ------------------------------------------------------------

let test_lambda_binds_params = {
    Name = "Lambda: param IDs are bound; body refs to them not free"
    Run = fun () ->
        // lambda(x: 10) -> x + 1
        let param: IRParam = { Name = "x"; Type = intTy; Index = 0; VarId = 10 }
        let body = IRBinOp (IRElementwise, IRAdd, IRVar (10, intTy), IRLit (IRLitInt 1L))
        let info: LambdaInfo = {
            Params = [param]; Body = body; Captures = []
            IsCommutative = false; CommGroups = []
        }
        let actual = exprAttrs (IRLambda info)
        let expected = mkAttrs [] [10] true
        (actual, expected)
}

let test_lambda_captures_outer = {
    Name = "Lambda: body refs to outer vars flow up as FreeVars"
    Run = fun () ->
        // lambda(x: 10) -> x + y    -- y is captured (outer)
        let param: IRParam = { Name = "x"; Type = intTy; Index = 0; VarId = 10 }
        let body = IRBinOp (IRElementwise, IRAdd, IRVar (10, intTy), IRVar (99, intTy))
        let info: LambdaInfo = {
            Params = [param]; Body = body; Captures = []
            IsCommutative = false; CommGroups = []
        }
        let actual = exprAttrs (IRLambda info)
        let expected = mkAttrs [99] [10] true
        (actual, expected)
}

let test_nested_lambda = {
    Name = "Lambda: inner lambda captures outer lambda's param"
    Run = fun () ->
        // lambda(x: 10) -> lambda(y: 20) -> x + y
        // From outermost view: FreeVars = {}; BoundVars = {10, 20}
        let innerParam: IRParam = { Name = "y"; Type = intTy; Index = 0; VarId = 20 }
        let innerBody = IRBinOp (IRElementwise, IRAdd, IRVar (10, intTy), IRVar (20, intTy))
        let innerLam = IRLambda {
            Params = [innerParam]; Body = innerBody; Captures = []
            IsCommutative = false; CommGroups = []
        }
        let outerParam: IRParam = { Name = "x"; Type = intTy; Index = 0; VarId = 10 }
        let outerLam = IRLambda {
            Params = [outerParam]; Body = innerLam; Captures = []
            IsCommutative = false; CommGroups = []
        }
        let actual = exprAttrs outerLam
        let expected = mkAttrs [] [10; 20] true
        (actual, expected)
}

/// ------------------------------------------------------------
/// 5. IRForRange and IRMatch binders
/// ------------------------------------------------------------

let test_forrange_binds_loop_var = {
    Name = "ForRange: loop varId is bound; lo/hi can have free vars"
    Run = fun () ->
        // for i:30 in 0..n { i + acc }
        // FreeVars: {n, acc}; BoundVars: {i (30)}
        let body = IRBinOp (IRElementwise, IRAdd, IRVar (30, intTy), IRVar (5, intTy))
        let e = IRForRange (30, IRLit (IRLitInt 0L), IRVar (99, intTy), body)
        let actual = exprAttrs e
        let expected = mkAttrs [99; 5] [30] true
        (actual, expected)
}

let test_match_pattern_binds = {
    Name = "Match: pattern var IDs are bound in case body"
    Run = fun () ->
        // match scrut(99) with
        //   | x_5 -> x_5 + y_8     // pattern binds 5; body refs 5 and 8
        let case: IRMatchCase = {
            Pattern = IRPatVar 5
            Guard = None
            Body = IRBinOp (IRElementwise, IRAdd, IRVar (5, intTy), IRVar (8, intTy))
        }
        let e = IRMatch (IRVar (99, intTy), [case])
        let actual = exprAttrs e
        // scrut contributes 99 free; case body has 5 bound, 8 free
        let expected = mkAttrs [99; 8] [5] true
        (actual, expected)
}

let test_match_tuple_pattern = {
    Name = "Match: tuple pattern binds multiple IDs"
    Run = fun () ->
        // match scrut(99) with
        //   | (a_5, b_6) -> a_5 + b_6 + c_8
        let case: IRMatchCase = {
            Pattern = IRPatTuple [IRPatVar 5; IRPatVar 6]
            Guard = None
            Body =
                IRBinOp (IRElementwise, IRAdd,
                    IRBinOp (IRElementwise, IRAdd, IRVar (5, intTy), IRVar (6, intTy)),
                    IRVar (8, intTy))
        }
        let e = IRMatch (IRVar (99, intTy), [case])
        let actual = exprAttrs e
        let expected = mkAttrs [99; 8] [5; 6] true
        (actual, expected)
}

/// ------------------------------------------------------------
/// 6. Every inline-form arm — guards against future arm omissions
/// ------------------------------------------------------------

let test_mask_arm = {
    Name = "Inline: IRMask unions array and predicate"
    Run = fun () ->
        // mask(a_1, p_2)
        let e = IRMask (IRVar (1, intTy), IRVar (2, intTy))
        let actual = exprAttrs e
        let expected = mkAttrs [1; 2] [] true
        (actual, expected)
}

let test_intersect_arm = {
    Name = "Inline: IRIntersect unions both arrays"
    Run = fun () ->
        let e = IRIntersect (IRVar (1, intTy), IRVar (2, intTy))
        let actual = exprAttrs e
        let expected = mkAttrs [1; 2] [] true
        (actual, expected)
}

let test_union_arm = {
    Name = "Inline: IRUnion unions both arrays"
    Run = fun () ->
        let e = IRUnion (IRVar (1, intTy), IRVar (2, intTy))
        let actual = exprAttrs e
        let expected = mkAttrs [1; 2] [] true
        (actual, expected)
}

let test_unique_arm = {
    Name = "Inline: IRUnique has the input array's free vars"
    Run = fun () ->
        let e = IRUnique (IRVar (1, intTy))
        let actual = exprAttrs e
        let expected = mkAttrs [1] [] true
        (actual, expected)
}

let test_contains_arm = {
    Name = "Inline: IRContains contributes a probe and unions array + value"
    Run = fun () ->
        // contains(a_1, v_2): probe carries BuildOn = a_1; the value
        // child is recursed into for FreeVars.
        let arr = IRVar (1, intTy)
        let e = IRContains (arr, IRVar (2, intTy))
        let actual = exprAttrs e
        let expected = mkAttrsWithProbes [1; 2] [] true [arr]
        (actual, expected)
}

let test_sort_arm = {
    Name = "Inline: IRSort unions array and key"
    Run = fun () ->
        let e = IRSort (IRVar (1, intTy), IRVar (2, intTy))
        let actual = exprAttrs e
        let expected = mkAttrs [1; 2] [] true
        (actual, expected)
}

let test_reduce_arm = {
    Name = "Inline: IRReduce unions array and kernel"
    Run = fun () ->
        let e = IRReduce (IRVar (1, intTy), IRVar (2, intTy))
        let actual = exprAttrs e
        let expected = mkAttrs [1; 2] [] true
        (actual, expected)
}

/// ------------------------------------------------------------
/// 7. Constructs that previously fell through collectVarRefsIR's
///    catch-all — verify exhaustive handling
/// ------------------------------------------------------------

let test_slice_finds_refs = {
    Name = "Exhaustiveness: IRSlice picks up start/stop refs"
    Run = fun () ->
        // arr_1[2:start_3:stop_4]
        let e = IRSlice (IRVar (1, intTy), 0, IRVar (3, intTy), IRVar (4, intTy))
        let actual = exprAttrs e
        let expected = mkAttrs [1; 3; 4] [] true
        (actual, expected)
}

let test_join_finds_refs = {
    Name = "Exhaustiveness: IRJoin picks up refs in joined arrays"
    Run = fun () ->
        let e = IRJoin ([IRVar (1, intTy); IRVar (2, intTy); IRVar (3, intTy)], 0)
        let actual = exprAttrs e
        let expected = mkAttrs [1; 2; 3] [] true
        (actual, expected)
}

let test_shift_with_pad = {
    Name = "Exhaustiveness: IRShift's BndPad expression is scanned"
    Run = fun () ->
        // shift(arr_1, dim, offset_2, BndPad fill_3)
        let e = IRShift (IRVar (1, intTy), 0, IRVar (2, intTy), BndPad (IRVar (3, intTy)))
        let actual = exprAttrs e
        let expected = mkAttrs [1; 2; 3] [] true
        (actual, expected)
}

/// ------------------------------------------------------------
/// 8. The key composition: nested binder + outer free var
/// (this is the semantic core that Phase C will rest on)
/// ------------------------------------------------------------

let test_mask_lambda_binder_does_not_leak = {
    Name = "Composition: lambda-bound param doesn't leak to outer FreeVars"
    Run = fun () ->
        // Mask with a contains-only predicate (the canonical pattern that
        // Phase C handles end-to-end at codegen). From outside the mask,
        // FreeVars should be {1, 2} (A and B). x_50 must NOT appear — it's
        // bound inside the lambda. The probe over B is consumed by the
        // mask, so the outer view also has Probes=[] (default empty).
        let xParam: IRParam = { Name = "x"; Type = intTy; Index = 0; VarId = 50 }
        let lamBody = IRContains (IRVar (2, intTy), IRVar (50, intTy))
        let lam = IRLambda {
            Params = [xParam]; Body = lamBody; Captures = []
            IsCommutative = false; CommGroups = []
        }
        let e = IRMask (IRVar (1, intTy), lam)
        let actual = exprAttrs e
        let expected = mkAttrs [1; 2] [50] true
        (actual, expected)
}

/// ------------------------------------------------------------
/// 9. Contains probe propagation
/// ------------------------------------------------------------
/// These tests validate the Phase C foundation: that any IRContains
/// node anywhere inside an expression flows up as a ContainsProbe
/// through every compositional construct, and that IRMask is the only
/// node that consumes them.

let test_probe_in_binop = {
    Name = "Probe: contains inside a binop propagates upward"
    Run = fun () ->
        // contains(B_1, x_2) && true_lit
        // → probe with BuildOn = B_1 reaches the top
        let arrB = IRVar (1, intTy)
        let cont = IRContains (arrB, IRVar (2, intTy))
        let e = IRBinOp (IRElementwise, IRAnd, cont, IRLit (IRLitBool true))
        let actual = exprAttrs e
        let expected = mkAttrsWithProbes [1; 2] [] true [arrB]
        (actual, expected)
}

let test_probe_in_if = {
    Name = "Probe: contains inside an if-branch propagates upward"
    Run = fun () ->
        // if cond_1 then contains(B_2, x_3) else false
        let arrB = IRVar (2, intTy)
        let cont = IRContains (arrB, IRVar (3, intTy))
        let e = IRIf (IRVar (1, boolTy), cont, IRLit (IRLitBool false))
        let actual = exprAttrs e
        let expected = mkAttrsWithProbes [1; 2; 3] [] true [arrB]
        (actual, expected)
}

let test_probe_through_nested_lambda = {
    Name = "Probe: contains inside a nested lambda body propagates upward"
    Run = fun () ->
        // Outer expression is the lambda itself — body has a contains.
        // From outside the lambda, the lambda's param is bound but the
        // contains's probe propagates up. (The lambda is NOT a mask.)
        let yParam: IRParam = { Name = "y"; Type = intTy; Index = 0; VarId = 30 }
        let arrB = IRVar (10, intTy)
        let body = IRContains (arrB, IRVar (30, intTy))
        let lam = IRLambda {
            Params = [yParam]; Body = body; Captures = []
            IsCommutative = false; CommGroups = []
        }
        let actual = exprAttrs lam
        // FreeVars: just 10 (the array); 30 is the lambda param, bound.
        // BoundVars: {30}. Probe propagates up.
        let expected = mkAttrsWithProbes [10] [30] true [arrB]
        (actual, expected)
}

let test_mask_consumes_probe = {
    Name = "Probe: IRMask consumes probes from its predicate"
    Run = fun () ->
        // mask(A_1, lambda(x_50) -> contains(B_2, x_50))
        // The contains generates a probe inside the predicate. The
        // mask consumes it. From outside, the OUTER view has no
        // probes. FreeVars and BoundVars accumulate normally.
        let arrB = IRVar (2, intTy)
        let cont = IRContains (arrB, IRVar (50, intTy))
        let xParam: IRParam = { Name = "x"; Type = intTy; Index = 0; VarId = 50 }
        let pred = IRLambda {
            Params = [xParam]; Body = cont; Captures = []
            IsCommutative = false; CommGroups = []
        }
        let e = IRMask (IRVar (1, intTy), pred)
        let actual = exprAttrs e
        // No probes at the outer level — mask consumed it.
        let expected = mkAttrs [1; 2] [50] true
        (actual, expected)
}

let test_mask_consumes_only_predicate_probes = {
    Name = "Probe: IRMask consumes ONLY predicate probes, not array-side"
    Run = fun () ->
        // mask(contains(B_1, k_2), lambda(x_50) -> contains(C_3, x_50))
        // The mask's predicate has a contains (consumed by mask). The
        // mask's array slot ALSO has a contains (unusual, but legal IR);
        // that one is not in the predicate, so it propagates up.
        let arrB = IRVar (1, intTy)
        let arrC = IRVar (3, intTy)
        let arrSide = IRContains (arrB, IRVar (2, intTy))
        let predBody = IRContains (arrC, IRVar (50, intTy))
        let xParam: IRParam = { Name = "x"; Type = intTy; Index = 0; VarId = 50 }
        let pred = IRLambda {
            Params = [xParam]; Body = predBody; Captures = []
            IsCommutative = false; CommGroups = []
        }
        let e = IRMask (arrSide, pred)
        let actual = exprAttrs e
        // arrSide's probe (BuildOn = B) survives; predBody's probe
        // (BuildOn = C) is consumed.
        let expected = mkAttrsWithProbes [1; 2; 3] [50] true [arrB]
        (actual, expected)
}

let test_multiple_probes_in_one_predicate = {
    Name = "Probe: two distinct contains calls in a predicate both collected"
    Run = fun () ->
        // contains(B_1, x_3) || contains(C_2, x_3)
        // Two probes, BuildOn = B_1 and C_2 respectively, in source order.
        let arrB = IRVar (1, intTy)
        let arrC = IRVar (2, intTy)
        let cont1 = IRContains (arrB, IRVar (3, intTy))
        let cont2 = IRContains (arrC, IRVar (3, intTy))
        let e = IRBinOp (IRElementwise, IROr, cont1, cont2)
        let actual = exprAttrs e
        let expected = mkAttrsWithProbes [1; 2; 3] [] true [arrB; arrC]
        (actual, expected)
}

let test_nested_mask_no_outer_probe = {
    Name = "Probe: contains inside an inner mask's predicate does not escape"
    Run = fun () ->
        // outer expression: mask(A_1, lambda(x_50) ->
        //   contains(mask(C_2, lambda(y_60) -> contains(B_3, y_60)), x_50))
        // The inner mask consumes its own probe (over B_3). The outer
        // mask's predicate then has a contains whose array is the inner
        // mask's result, so its probe propagates up to the OUTER mask,
        // which consumes it. Net: zero probes escape to the top level.
        let innerB = IRVar (3, intTy)
        let innerCont = IRContains (innerB, IRVar (60, intTy))
        let yParam: IRParam = { Name = "y"; Type = intTy; Index = 0; VarId = 60 }
        let innerPred = IRLambda {
            Params = [yParam]; Body = innerCont; Captures = []
            IsCommutative = false; CommGroups = []
        }
        let innerMask = IRMask (IRVar (2, intTy), innerPred)
        let outerCont = IRContains (innerMask, IRVar (50, intTy))
        let xParam: IRParam = { Name = "x"; Type = intTy; Index = 0; VarId = 50 }
        let outerPred = IRLambda {
            Params = [xParam]; Body = outerCont; Captures = []
            IsCommutative = false; CommGroups = []
        }
        let e = IRMask (IRVar (1, intTy), outerPred)
        let actual = exprAttrs e
        // No probes at top level (both consumed).
        let expected = mkAttrs [1; 2; 3] [50; 60] true
        (actual, expected)
}

let test_probe_in_let_value = {
    Name = "Probe: contains inside a let-value propagates upward"
    Run = fun () ->
        // let z_10 = contains(B_1, x_2) in z_10
        // The probe in the let-value flows up through the let.
        let arrB = IRVar (1, intTy)
        let cont = IRContains (arrB, IRVar (2, intTy))
        let e = IRLet (10, cont, IRVar (10, boolTy))
        let actual = exprAttrs e
        // FreeVars: {1, 2}; BoundVars: {10}; probe propagates.
        let expected = mkAttrsWithProbes [1; 2] [10] true [arrB]
        (actual, expected)
}

/// All Phase B + Phase C foundation corpus tests, in registration order.
let allAttrsTests : AttrsTest list = [
    test_lit_has_no_refs
    test_var_is_free
    test_param_has_no_refs
    test_binop_unions_children
    test_unaryop_passes_through
    test_tuple_unions_all
    test_let_binds_id
    test_let_shadowing
    test_let_value_can_be_free
    test_lambda_binds_params
    test_lambda_captures_outer
    test_nested_lambda
    test_forrange_binds_loop_var
    test_match_pattern_binds
    test_match_tuple_pattern
    test_mask_arm
    test_intersect_arm
    test_union_arm
    test_unique_arm
    test_contains_arm
    test_sort_arm
    test_reduce_arm
    test_slice_finds_refs
    test_join_finds_refs
    test_shift_with_pad
    test_mask_lambda_binder_does_not_leak
    // Phase C foundation: contains-probe propagation
    test_probe_in_binop
    test_probe_in_if
    test_probe_through_nested_lambda
    test_mask_consumes_probe
    test_mask_consumes_only_predicate_probes
    test_multiple_probes_in_one_predicate
    test_nested_mask_no_outer_probe
    test_probe_in_let_value
]

/// Pretty-print an ExprAttrs for diffing. The sets are printed sorted so
/// the output is stable regardless of insertion order. Probes are listed
/// in collection order with just their BuildOn shown (Node is reference-
/// equality data, not human-readable).
let private fmtAttrs (a: ExprAttrs) : string =
    let setStr (s: Set<IRId>) =
        s |> Set.toList |> List.sort
          |> List.map string |> String.concat ", "
    let probesStr =
        a.Probes
        |> List.map (fun p -> sprintf "%A" p.BuildOn)
        |> String.concat ", "
    sprintf "{ Free={%s}; Bound={%s}; Pure=%b; Probes=[%s] }"
        (setStr a.FreeVars) (setStr a.BoundVars) a.IsPure probesStr

let private attrsEqual (a: ExprAttrs) (b: ExprAttrs) : bool =
    // Probes compared by BuildOn (structural) and length; the Node field
    // is for codegen reference-identity use and isn't meaningful in tests.
    let probesMatch =
        a.Probes.Length = b.Probes.Length &&
        List.zip a.Probes b.Probes
        |> List.forall (fun (pa, pb) -> pa.BuildOn = pb.BuildOn)
    a.FreeVars = b.FreeVars
        && a.BoundVars = b.BoundVars
        && a.IsPure = b.IsPure
        && probesMatch

/// Run all attribute tests, returning (passed, failed). Prints per-test
/// status; on failure also prints actual vs expected so the difference
/// is visible without re-running with extra flags.
let runAttrsTests () : int * int =
    printfn ""
    printfn "=== Phase B: ExprAttrs corpus ==="
    let mutable passed = 0
    let mutable failed = 0
    for t in allAttrsTests do
        let (actual, expected) = t.Run ()
        if attrsEqual actual expected then
            passed <- passed + 1
            printfn "  [OK] %s" t.Name
        else
            failed <- failed + 1
            printfn "  [FAIL] %s" t.Name
            printfn "    expected: %s" (fmtAttrs expected)
            printfn "    actual:   %s" (fmtAttrs actual)
    printfn "ExprAttrs Tests: %d passed, %d failed" passed failed
    (passed, failed)

