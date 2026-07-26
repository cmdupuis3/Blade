// peakmem.fsx — run a program and report its PEAK WORKING SET.
// Usage:  dotnet fsi examples/tools/peakmem.fsx <exe> [args...]
// Written for the deterministic-deallocation gate: the interesting number for
// examples/09 and tests/corpus/memfree-stress/011 is the high-water mark, not
// the exit-time footprint. PeakWorkingSet64 is sampled while the child runs
// (reading it after exit throws on some runtimes), so the max of the samples
// is the authority.
open System
open System.Diagnostics

let argv = Environment.GetCommandLineArgs()
let idx = argv |> Array.tryFindIndex (fun a -> a.EndsWith("peakmem.fsx", StringComparison.OrdinalIgnoreCase))
let rest = match idx with Some k -> argv.[k + 1 ..] | None -> [||]
if rest.Length = 0 then
    eprintfn "usage: dotnet fsi peakmem.fsx <exe> [args...]"
    exit 2

let quote (s: string) = if s.Contains " " then "\"" + s + "\"" else s
let exePath = IO.Path.GetFullPath rest.[0]
let psi = ProcessStartInfo(exePath, rest.[1..] |> Array.map quote |> String.concat " ")
psi.RedirectStandardOutput <- true
psi.RedirectStandardError <- true
psi.UseShellExecute <- false
psi.WorkingDirectory <- IO.Path.GetDirectoryName exePath
let p = Process.Start psi
p.OutputDataReceived.Add(fun e -> if not (isNull e.Data) then Console.Out.WriteLine e.Data)
p.ErrorDataReceived.Add(fun e -> if not (isNull e.Data) then Console.Error.WriteLine e.Data)
p.BeginOutputReadLine()
p.BeginErrorReadLine()

// 100 ms sampling: short-lived children can still slip under one interval —
// for those the printed peak reads 0 MB (the number only matters for the
// minutes-long simulation gates).
let mutable peak = 0L
while not (p.WaitForExit 100) do
    try
        p.Refresh()
        peak <- max peak p.PeakWorkingSet64
    with _ -> ()
try
    p.Refresh()
    peak <- max peak p.PeakWorkingSet64
with _ -> ()
p.WaitForExit() // flush the async readers
printfn "PEAK WORKING SET: %d MB" (peak / 1048576L)
exit p.ExitCode
