namespace MomentAlgebra

/// Minimal pass/fail harness, mirroring BladeML's style: silent on pass,
/// loud on fail, summary + exit code at the end.
module TestHarness =

    let mutable passed = 0
    let mutable failed = 0
    let private failures = ResizeArray<string>()

    let section (name: string) =
        printfn "-- %s" name

    let check (name: string) (cond: bool) =
        if cond then
            passed <- passed + 1
        else
            failed <- failed + 1
            failures.Add name
            printfn "  FAIL %s" name

    let checkClose (name: string) (tol: float) (expected: float) (actual: float) =
        check (sprintf "%s (expected %.12g, got %.12g, tol %g)" name expected actual tol)
              (abs (expected - actual) <= tol)

    /// Tolerance = max(relTol * |expected|, absTol) — for statistical checks.
    let checkCloseRel (name: string) (relTol: float) (absTol: float) (expected: float) (actual: float) =
        let tol = max (relTol * abs expected) absTol
        checkClose name tol expected actual

    let checkArrayClose (name: string) (tol: float) (expected: float[]) (actual: float[]) =
        if expected.Length <> actual.Length then
            check (sprintf "%s (length %d vs %d)" name expected.Length actual.Length) false
        else
            let mutable maxDiff = 0.0
            for i in 0 .. expected.Length - 1 do
                maxDiff <- max maxDiff (abs (expected.[i] - actual.[i]))
            check (sprintf "%s (max abs diff %.3g, tol %g)" name maxDiff tol) (maxDiff <= tol)

    let checkThrows (name: string) (f: unit -> unit) =
        let threw =
            try
                f ()
                false
            with _ -> true
        check (sprintf "%s (expected exception)" name) threw

    let summary () : int =
        printfn ""
        printfn "MomentAlgebra: %d passed, %d failed" passed failed
        if failed > 0 then
            for f in failures do
                printfn "  FAILED: %s" f
        if failed > 0 then 1 else 0
