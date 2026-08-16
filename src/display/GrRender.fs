// GR renders: the serve process's half of the `blade-gr-render` worker
// (Blade-REPL/docs/gr-graphics-plan.md section 3, 4.2, 7).
//
// A GR render is a POST-HOC transformation of a spec the program already
// emitted -- the plotly trace JSON the panel retains per plot -- so nothing
// here touches either program lane, the display-frame format, or the
// differential gate. `ide serve` asks for one when the user flips the panel's
// backend toggle; this module owns the native worker that answers.
//
// Why a persistent worker rather than one process per render: GR's cold start
// is ~2.6 s while a warm render is 33-350 ms, and PNG bytes are proven
// process-state-independent (a long-lived worker renders byte-identical output
// to a fresh one), so the toggle can feel instant without weakening any
// determinism claim. A GR fault kills the worker, not serve; the next request
// respawns it.
//
// NDJSON over the worker's stdin/stdout, one request at a time (serveLoop
// handles one request at a time, so there is no concurrency machinery here and
// no lock to get wrong):
//
//   -> {"id":N,"cmd":"render","spec":{...},"width":W,"height":H,"format":"png"}
//   <- {"id":N,"ok":true,"format":"png","data":"<base64>"}
//   <- {"id":N,"ok":false,"error":"..."}
//   -> {"id":N,"cmd":"ping"}      <- {"id":N,"ok":true}
//   -> {"cmd":"shutdown"}         (also exits on stdin EOF)
//
// `spec` is spliced into the request VERBATIM, exactly as IdeServe.evalResponse
// splices display frames: it arrived as JSON text and a reparse-and-reserialize
// round trip through any number type would be free to move the last digit of
// every coordinate in the plot.
//
// THE ENVIRONMENT IS LOAD-BEARING (all three failure modes are SILENT crashes,
// measured -- see the plan's section 7):
//
//   GRDIR unset          -> STATUS_ACCESS_VIOLATION inside GR
//   %GRDIR%\bin not on PATH -> STATUS_DLL_NOT_FOUND
//   GKS_WSTYPE unset     -> GR's default Windows workstation is gksqt, which can
//                           leave a lingering Qt process behind; 100 is the null
//                           device and does not affect gr_beginprint output
//   GR_DISPLAY set       -> same gksqt hazard, so it is REMOVED from the child
//
// That is why availability is pre-validated here (a missing GRDIR is reported
// as a reason string, never spawned into) rather than discovered as a crash.
//
// Compiles before IdeServe.fs, its only consumer. Depends on nothing but the
// BCL.
module Blade.Display.GrRender

open System
open System.Diagnostics
open System.IO
open System.Runtime.InteropServices
open System.Text
open System.Text.Json
open System.Threading.Tasks

// ---------------------------------------------------------------------------
// Resolution: which helper, and is GR really there
// ---------------------------------------------------------------------------

/// The helper's file name. A native exe both places it can be found.
let private helperLeaf = if OperatingSystem.IsWindows() then "gr-render.exe" else "gr-render"

/// Platform-arch stamp for the PREBUILT artifact naming convention, e.g.
/// `win32-x64`, `linux-x64`, `darwin-arm64`. Deliberately spelled the way
/// Node's `process.platform`/`process.arch` spell them (`win32`, not
/// `windows`; `darwin`, not `macos`) because tools/gr-render/package.ps1,
/// deps.json's `gr` asset keys, and the Blade-REPL extension's fetch-vendor
/// script (a sibling npm/Node project) all key off the same pair -- one
/// vocabulary for "which platform" across the whole toolchain, not a second
/// one invented on the .NET side.
let private platformTag () : string =
    let os =
        if OperatingSystem.IsWindows() then "win32"
        elif OperatingSystem.IsMacOS() then "darwin"
        elif OperatingSystem.IsLinux() then "linux"
        else "unknown"
    let arch =
        match RuntimeInformation.OSArchitecture with
        | Architecture.X64 -> "x64"
        | Architecture.Arm64 -> "arm64"
        | other -> (string other).ToLowerInvariant()
    sprintf "%s-%s" os arch

/// The platform-stamped artifact name gr-render/package.ps1 emits, e.g.
/// `gr-render-win32-x64.exe`. Checked BEFORE the plain `helperLeaf` at every
/// search location below: a release/CI build drops one of these per platform
/// next to (or walking up from) Blade.exe, and it should win over a plain
/// `gr-render.exe` that might be a leftover from a different platform's copy
/// or an older manual build.
let stampedHelperLeaf =
    let ext = if OperatingSystem.IsWindows() then ".exe" else ""
    sprintf "gr-render-%s%s" (platformTag ()) ext

/// Names tried at each search location, in preference order: the
/// platform-stamped prebuilt first, the plain name second. `distinct` just
/// guards the degenerate case where `platformTag` returns "unknown-unknown"
/// and somehow collides with the plain name; in practice the two are always
/// different strings.
let private candidateLeaves = [ stampedHelperLeaf; helperLeaf ] |> List.distinct

/// How many directory levels above the running binary are searched for
/// `tools/gr-render/`. A dev checkout runs Blade.exe out of
/// `bin/Release/net7.0/` (3 levels), the test harness out of scratch dirs
/// below that; 8 covers those without walking to the drive root.
[<Literal>]
let MaxWalkUp = 8

/// Per-render deadline. Mutable so the fake-helper tests can exercise the
/// timeout path (which kills the worker) in milliseconds instead of half a
/// minute; nothing in the product writes to it.
let mutable renderTimeoutMs = 30000

/// Deadline for the liveness ping sent right after a spawn. Shorter than a
/// render because it does no GR work -- a worker that cannot answer it is not
/// going to render anything either.
let mutable pingTimeoutMs = 10000

/// Where the helper is, or why it isn't. The order is: an explicit
/// BLADE_GR_RENDER (a wrong explicit setting is an ERROR, never a silent
/// fallthrough; unchanged by the platform-stamped naming below -- an explicit
/// path is used exactly as given, whatever it's named), then beside the
/// running Blade.exe (a deployed toolchain), then `tools/gr-render/` above it
/// (a dev checkout, where Blade.exe runs from a bin directory under the repo
/// root). AT EACH of those last two locations, a platform-stamped prebuilt
/// (`gr-render-<platform>-<arch>[.exe]`) is preferred over the plain
/// `gr-render[.exe]` if both are present -- see `candidateLeaves`. Factored
/// out of `resolveHelper` (which fixes `baseDir` to `AppContext.BaseDirectory`)
/// so tests can drive the walk-up logic against a scratch directory tree
/// without needing to relocate the running process.
let resolveHelperFrom (baseDir: string) : Result<string, string> =
    let explicitPath = Environment.GetEnvironmentVariable "BLADE_GR_RENDER"
    if not (String.IsNullOrWhiteSpace explicitPath) then
        let p = explicitPath.Trim()
        if File.Exists p then Ok (Path.GetFullPath p)
        else Error (sprintf "gr-render helper not found: BLADE_GR_RENDER points at '%s', which does not exist" p)
    else
        let besideMatch =
            candidateLeaves
            |> List.map (fun leaf -> Path.Combine(baseDir, leaf))
            |> List.tryFind File.Exists
        match besideMatch with
        | Some p -> Ok p
        | None ->
            let rec walk (dir: DirectoryInfo) (depth: int) =
                if isNull dir || depth > MaxWalkUp then None
                else
                    let hereMatch =
                        candidateLeaves
                        |> List.tryPick (fun leaf ->
                            let cand = Path.Combine(dir.FullName, "tools", "gr-render", leaf)
                            if File.Exists cand then Some cand else None)
                    match hereMatch with
                    | Some p -> Some p
                    | None -> walk dir.Parent (depth + 1)
            match walk (DirectoryInfo baseDir) 0 with
            | Some p -> Ok p
            | None ->
                let names = String.concat " or " candidateLeaves
                Error (sprintf "gr-render helper not found (looked for %s beside %s and in tools/gr-render up to %d levels above it; set BLADE_GR_RENDER to override)"
                               names (baseDir.TrimEnd(Path.DirectorySeparatorChar)) MaxWalkUp)

let resolveHelper () : Result<string, string> = resolveHelperFrom AppContext.BaseDirectory

/// A validated GR installation: the root and its `bin` (which is what has to
/// be on the child's PATH, and what holds libGR.dll).
type GrEnv = { GrDir: string; GrBin: string }

/// GR's own environment, PRE-VALIDATED. Both failure modes below are silent
/// crashes if spawned into, so they are reasons, not exceptions.
let resolveGrEnv () : Result<GrEnv, string> =
    let grdir = Environment.GetEnvironmentVariable "GRDIR"
    if String.IsNullOrWhiteSpace grdir then Error "GR unavailable: GRDIR not set"
    else
        let root = grdir.Trim()
        let bin = Path.Combine(root, "bin")
        if Directory.Exists bin then Ok { GrDir = root; GrBin = bin }
        else Error (sprintf "GR unavailable: GRDIR bin missing (%s does not exist)" bin)

/// Can this process render at all?
type Availability =
    /// The helper that would be spawned.
    | Available of string
    /// Why not, in words a user can act on.
    | Unavailable of string

/// Pure resolution -- spawns nothing, so a client may call it to decide
/// whether to offer a GR toggle at all.
let availability () : Availability =
    match resolveHelper () with
    | Error reason -> Unavailable reason
    | Ok helper ->
        match resolveGrEnv () with
        | Error reason -> Unavailable reason
        | Ok _ -> Available helper

// ---------------------------------------------------------------------------
// The worker
// ---------------------------------------------------------------------------

type private Worker = { Proc: Process; Stdin: StreamWriter; Stdout: StreamReader }

/// One worker per serve process, spawned lazily and reused. Plain mutables:
/// serveLoop handles one request at a time (IdeServe.serveLoop's doc comment),
/// which is the same reason nothing here needs a lock.
let mutable private worker : Worker option = None
let mutable private nextId = 0

/// How to launch `helper --serve`. The real helper is a native exe and takes
/// the first arm. The others exist because BLADE_GR_RENDER is an escape hatch
/// people point at wrapper scripts (and the fake-helper tests point at exactly
/// such a script): .NET spawns through CreateProcess, which cannot run a .cmd
/// or a .ps1 directly.
let private argvFor (helper: string) : string * string list =
    match (Path.GetExtension helper).ToLowerInvariant() with
    | ".cmd" | ".bat" -> "cmd.exe", [ "/d"; "/c"; helper; "--serve" ]
    | ".ps1" -> "powershell.exe", [ "-NoProfile"; "-ExecutionPolicy"; "Bypass"; "-File"; helper; "--serve" ]
    | _ -> helper, [ "--serve" ]

/// Kill the current worker (if any) and forget it. Idempotent, never throws:
/// every failure path calls it, including ones racing the process's own exit.
/// The whole TREE goes, because a wrapper script's shell is the parent of the
/// process that actually holds the pipes.
let private killWorker () =
    match worker with
    | None -> ()
    | Some w ->
        worker <- None
        (try (if not w.Proc.HasExited then w.Proc.Kill true) with _ -> ())
        (try w.Proc.Dispose() with _ -> ())

/// Is a worker currently up? Exposed for the tests, which assert that a
/// malformed or timed-out response TOOK THE WORKER DOWN -- the invariant that
/// makes the next request a clean respawn rather than a desynchronized read.
let workerAlive () : bool =
    match worker with
    | Some w -> (try not w.Proc.HasExited with _ -> false)
    | None -> false

let private writeLine (w: Worker) (line: string) : Result<unit, string> =
    try
        w.Stdin.Write line
        // Explicit '\n': WriteLine would emit CRLF on Windows, and the wire is
        // NDJSON exactly like serve's own.
        w.Stdin.Write '\n'
        w.Stdin.Flush ()
        Ok ()
    with ex -> Error (sprintf "gr-render worker is not accepting requests: %s" ex.Message)

/// One response line, or a reason. A null read is EOF -- the worker died
/// mid-request, which is the crash-isolation case: report it and let the next
/// request respawn.
let private readLineWithin (w: Worker) (timeoutMs: int) : Result<string, string> =
    let t = Task.Run(fun () -> w.Stdout.ReadLine())
    let completed = try t.Wait timeoutMs with _ -> true
    if not completed then
        Error (sprintf "gr-render worker timed out after %d ms" timeoutMs)
    else
        match (try Ok t.Result with ex -> Error ex.Message) with
        | Error msg -> Error (sprintf "gr-render worker went away: %s" msg)
        | Ok null -> Error "gr-render worker exited without answering"
        | Ok line -> Ok line

let private spawn (helper: string) (gr: GrEnv) : Result<Worker, string> =
    try
        let (exe, args) = argvFor helper
        let psi = ProcessStartInfo(exe)
        for a in args do psi.ArgumentList.Add a
        psi.UseShellExecute <- false
        psi.CreateNoWindow <- true
        psi.RedirectStandardInput <- true
        psi.RedirectStandardOutput <- true
        psi.RedirectStandardError <- true
        psi.StandardInputEncoding <- UTF8Encoding(false)
        psi.StandardOutputEncoding <- UTF8Encoding(false)
        psi.StandardErrorEncoding <- UTF8Encoding(false)
        // Not the user's project directory: GR can drop stray gks.png/gks.pdf
        // files into the process cwd, and serve chdirs per request into
        // whatever the editor is checking.
        (try
            let dir = Path.GetDirectoryName helper
            if not (String.IsNullOrEmpty dir) && Directory.Exists dir then psi.WorkingDirectory <- dir
         with _ -> ())
        // The four env settings the module header calls load-bearing. The
        // dictionary starts as a copy of THIS process's environment, so
        // everything else is inherited.
        psi.Environment.["GRDIR"] <- gr.GrDir
        psi.Environment.["GKS_WSTYPE"] <- "100"
        psi.Environment.Remove "GR_DISPLAY" |> ignore
        let existingPath =
            match psi.Environment.TryGetValue "PATH" with
            | true, v when not (isNull v) -> v
            | _ -> ""
        psi.Environment.["PATH"] <-
            if existingPath = "" then gr.GrBin
            else gr.GrBin + string Path.PathSeparator + existingPath
        let p = new Process(StartInfo = psi)
        if not (p.Start()) then Error (sprintf "could not start the gr-render helper (%s)" helper)
        else
            // stderr is free-form worker logging. It MUST be drained or a
            // chatty worker fills the pipe buffer and blocks forever mid-render.
            p.ErrorDataReceived.Add(fun e -> if not (isNull e.Data) then eprintfn "[gr-render] %s" e.Data)
            p.BeginErrorReadLine()
            Ok { Proc = p; Stdin = p.StandardInput; Stdout = p.StandardOutput }
    with ex ->
        Error (sprintf "could not start the gr-render helper (%s): %s" helper ex.Message)

/// Spawn + liveness ping. The ping is what turns "the exe started" into "the
/// exe speaks this protocol": a helper from a different build, or one that dies
/// during GR initialization, fails here instead of at the first render.
let private startWorker () : Result<Worker, string> =
    match resolveHelper () with
    | Error reason -> Error reason
    | Ok helper ->
        match resolveGrEnv () with
        | Error reason -> Error reason
        | Ok gr ->
            match spawn helper gr with
            | Error reason -> Error reason
            | Ok w ->
                worker <- Some w
                nextId <- nextId + 1
                let id = nextId
                let ping = sprintf "{\"id\":%d,\"cmd\":\"ping\"}" id
                let ok =
                    match writeLine w ping with
                    | Error e -> Error e
                    | Ok () ->
                        match readLineWithin w pingTimeoutMs with
                        | Error e -> Error e
                        | Ok line ->
                            try
                                use doc = JsonDocument.Parse line
                                let root = doc.RootElement
                                let okProp =
                                    match root.TryGetProperty "ok" with
                                    | true, v -> v.ValueKind = JsonValueKind.True
                                    | _ -> false
                                if okProp then Ok ()
                                else Error (sprintf "gr-render worker did not answer its startup ping with ok:true (%s)" line)
                            with ex ->
                                Error (sprintf "gr-render worker sent a malformed startup ping response: %s" ex.Message)
                match ok with
                | Ok () -> Ok w
                | Error reason ->
                    killWorker ()
                    Error reason

let private ensureWorker () : Result<Worker, string> =
    match worker with
    | Some w when (try not w.Proc.HasExited with _ -> false) -> Ok w
    | _ ->
        killWorker ()
        startWorker ()

/// Render one spec. `spec` is the RAW JSON text of a figure object and is
/// spliced into the request verbatim (see the module header). Returns the
/// worker's base64 payload, or a reason naming the actual problem.
///
/// Every failure that could have desynchronized the pipe -- a timeout, a
/// malformed line, an id that isn't the one we asked about -- kills the worker,
/// so the next call starts from a clean process. An `ok:false` answer does NOT:
/// the worker is alive and answering correctly, it just could not draw this
/// spec.
let render (spec: string) (width: int) (height: int) (format: string) : Result<string, string> =
    match ensureWorker () with
    | Error reason -> Error reason
    | Ok w ->
        nextId <- nextId + 1
        let id = nextId
        let req =
            sprintf "{\"id\":%d,\"cmd\":\"render\",\"spec\":%s,\"width\":%d,\"height\":%d,\"format\":\"%s\"}"
                    id spec width height format
        let malformed (detail: string) =
            killWorker ()
            Error (sprintf "gr-render worker sent a malformed response: %s" detail)
        match writeLine w req with
        | Error reason ->
            killWorker ()
            Error reason
        | Ok () ->
            match readLineWithin w renderTimeoutMs with
            | Error reason ->
                killWorker ()
                Error reason
            | Ok line ->
                let parsed =
                    try Ok (JsonDocument.Parse line)
                    with ex -> Error ex.Message
                match parsed with
                | Error msg -> malformed (sprintf "%s -- %s" msg (line.Substring(0, min 200 line.Length)))
                | Ok doc ->
                    use doc = doc
                    let root = doc.RootElement
                    if root.ValueKind <> JsonValueKind.Object then
                        malformed (line.Substring(0, min 200 line.Length))
                    else
                        let respId =
                            match root.TryGetProperty "id" with
                            | true, v when v.ValueKind = JsonValueKind.Number ->
                                (match v.TryGetInt32() with true, n -> Some n | _ -> None)
                            | _ -> None
                        let okKind =
                            match root.TryGetProperty "ok" with
                            | true, v -> Some v.ValueKind
                            | _ -> None
                        if respId <> Some id then
                            // A response for a request we are not waiting on
                            // means the pipe is out of step; nothing later on
                            // it can be trusted.
                            malformed (sprintf "expected id %d, got %A" id respId)
                        else
                            match okKind with
                            | Some JsonValueKind.True ->
                                match root.TryGetProperty "data" with
                                | true, v when v.ValueKind = JsonValueKind.String -> Ok (v.GetString())
                                | _ -> malformed "ok:true without a \"data\" string"
                            | Some JsonValueKind.False ->
                                // The worker is fine -- keep it.
                                let msg =
                                    match root.TryGetProperty "error" with
                                    | true, v when v.ValueKind = JsonValueKind.String -> v.GetString()
                                    | _ -> "(no reason given)"
                                Error (sprintf "render failed: %s" msg)
                            | _ -> malformed "response carries no boolean \"ok\""

/// Stop the worker. Called when the serve process is going down; best-effort
/// clean shutdown first, then the kill that actually guarantees it. Safe when
/// nothing is running, and safe to call twice.
let shutdown () =
    match worker with
    | Some w -> (try writeLine w "{\"cmd\":\"shutdown\"}" |> ignore with _ -> ())
    | None -> ()
    killWorker ()
