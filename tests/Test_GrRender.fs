// The GR render lane (Blade-REPL/docs/gr-graphics-plan.md section 4.2): the
// `renderPlot` response BYTES, the request's argument rules, helper/GRDIR
// resolution, and the worker protocol itself.
//
// Three tiers, deliberately, because only the last one needs anything
// installed:
//
//   1. Pure encoder/resolver checks. No process, no GR, no helper.
//   2. The worker protocol against a FAKE helper -- a PowerShell script this
//      file writes into a temp directory that speaks the NDJSON contract with
//      canned answers. This is what pins the parts that only a live worker can
//      show: the request line's verbatim spec splice, the child's environment
//      (the four settings whose absence is a SILENT GR crash), and every
//      failure mode -- a refused spec, a garbled line, a worker that dies
//      mid-request, a render that never returns. Windows-only (it spawns
//      powershell.exe); reports Skip elsewhere.
//   3. One end-to-end case against a REAL gr-render + GR install, gated on
//      availability exactly as the g++ blocks gate on a toolchain: it skips
//      with a message when BLADE_GR_RENDER/GRDIR name nothing usable, and is
//      what proves the wire actually carries a PNG once the helper exists.
//
// Note what is NOT here: any change to the display-frame format, either
// program lane, or the differential gate. A GR render is a post-hoc transform
// of an already-emitted spec, so tests/InterpDiff.fs is untouched by all of it.
module Blade.Tests.GrRender

open System
open System.IO
open Blade.Tests.TestHarness

module GR = Blade.Display.GrRender

// ---------------------------------------------------------------------------
// Harness plumbing
// ---------------------------------------------------------------------------

/// Feed a conversation through the real serve loop, in-process, and split the
/// transcript on the framing newline (same seam Cli.fs's IdeServe block uses).
let private drive (requests: string list) : string list =
    let input = new StringReader(String.concat "\n" requests + "\n")
    let output = new StringWriter()
    Blade.IdeServe.serveLoop "test" (input :> TextReader) (output :> TextWriter) |> ignore
    output.ToString().Split('\n')
    |> Array.filter (fun s -> s <> "")
    |> List.ofArray

/// Set environment variables for the duration of `f`, restoring whatever was
/// there before -- including "was not set at all", which is the state half of
/// these tests are ABOUT.
let private withEnv (pairs: (string * string option) list) (f: unit -> 'a) : 'a =
    let saved = pairs |> List.map (fun (k, _) -> k, Environment.GetEnvironmentVariable k)
    try
        for (k, v) in pairs do
            Environment.SetEnvironmentVariable(k, match v with Some s -> s | None -> null)
        f ()
    finally
        for (k, v) in saved do Environment.SetEnvironmentVariable(k, v)

/// The fake helper. One PowerShell script, `mode` baked in, speaking the
/// worker contract from the GrRender header:
///
///   ping   -> {"id":N,"ok":true}, always and immediately (every mode has to
///             survive the spawn-time liveness ping)
///   render -> per mode, after recording the request line VERBATIM and the
///             environment it was handed
///
/// Modes: `ok` (a canned base64 payload), `fail` (a well-formed ok:false),
/// `garbage` (a line that is not JSON), `dieonce` (exit without answering the
/// first render, answer the next one -- the crash-isolation case, which needs
/// the marker file to survive the process), `slow` (sleep past the deadline).
let private fakeScript (mode: string) (logPath: string) (envPath: string) (markerPath: string) =
    let tmpl = """
$ErrorActionPreference = 'Stop'
$log = '__LOG__'
$envOut = '__ENVOUT__'
$marker = '__MARKER__'
$mode = '__MODE__'
$p0 = ($env:PATH -split ';')[0]
[IO.File]::WriteAllText($envOut, "GRDIR=$env:GRDIR|WSTYPE=$env:GKS_WSTYPE|DISPLAY=[$env:GR_DISPLAY]|PATH0=$p0")
function Reply($s) { [Console]::Out.Write($s + "`n"); [Console]::Out.Flush() }
while ($true) {
    $line = [Console]::In.ReadLine()
    if ($null -eq $line) { break }
    $id = '0'
    if ($line -match '"id":(\d+)') { $id = $matches[1] }
    if ($line -match '"cmd":"ping"') { Reply ('{"id":' + $id + ',"ok":true}'); continue }
    [IO.File]::WriteAllText($log, $line)
    switch ($mode) {
        'ok'      { Reply ('{"id":' + $id + ',"ok":true,"format":"png","data":"QUJD"}') }
        'fail'    { Reply ('{"id":' + $id + ',"ok":false,"error":"contourf: unsupported trace"}') }
        'garbage' { Reply 'this is not json' }
        'slow'    { Start-Sleep -Milliseconds 3000; Reply ('{"id":' + $id + ',"ok":true,"format":"png","data":"QUJD"}') }
        'dieonce' {
            if (Test-Path -LiteralPath $marker) { Reply ('{"id":' + $id + ',"ok":true,"format":"png","data":"QUJD"}') }
            else { [IO.File]::WriteAllText($marker, 'x'); exit 1 }
        }
    }
}
"""
    tmpl.Replace("__LOG__", logPath).Replace("__ENVOUT__", envPath)
        .Replace("__MARKER__", markerPath).Replace("__MODE__", mode)

// ---------------------------------------------------------------------------
// The block
// ---------------------------------------------------------------------------

let runGrRenderTests () : BlockResult =
    let blockName = "GR Render"
    printHeader "renderPlot (frame bytes, request rules, worker protocol)"
    let results = ResizeArray<string * Outcome>()
    let record name outcome detail =
        resultLine outcome name detail
        results.Add((name, outcome))
    /// expected = actual, or a Fail carrying both.
    let pin name (expected: string) (actual: string) =
        if expected = actual then record name Pass ""
        else record name Fail (sprintf "expected %s, got %s" expected actual)
    let ok name (cond: bool) (detail: string) =
        if cond then record name Pass "" else record name Fail detail

    // ---- 1. Response wire bytes ------------------------------------------
    //
    // The client feeds these straight to its display-frame decoder, so the
    // bytes ARE the contract: `v`/`encoding` come from Blade.Display.Frame (one
    // owner of the format), `meta.id` is echoed from the request so the panel
    // merges rather than appends, and `backend` is what makes the panel show
    // this render under its GR toggle.

    pin "renderPlot response: png frame with a plotId"
        "{\"id\":7,\"frame\":{\"v\":1,\"mime\":\"image/png\",\"encoding\":\"base64\",\"data\":\"iVBOR\",\"meta\":{\"id\":\"blade-3\",\"backend\":\"gr\"}}}"
        (Blade.IdeServe.renderPlotResponse 7 "png" (Some "blade-3") "iVBOR")

    pin "renderPlot response: no plotId leaves meta carrying only the backend"
        "{\"id\":7,\"frame\":{\"v\":1,\"mime\":\"image/png\",\"encoding\":\"base64\",\"data\":\"iVBOR\",\"meta\":{\"backend\":\"gr\"}}}"
        (Blade.IdeServe.renderPlotResponse 7 "png" None "iVBOR")

    pin "renderPlot response: svg travels base64 under image/svg+xml"
        "{\"id\":1,\"frame\":{\"v\":1,\"mime\":\"image/svg+xml\",\"encoding\":\"base64\",\"data\":\"PHN2Zz4=\",\"meta\":{\"backend\":\"gr\"}}}"
        (Blade.IdeServe.renderPlotResponse 1 "svg" None "PHN2Zz4=")

    pin "renderPlot response: pdf travels base64 under application/pdf"
        "{\"id\":1,\"frame\":{\"v\":1,\"mime\":\"application/pdf\",\"encoding\":\"base64\",\"data\":\"JVBERi0=\",\"meta\":{\"backend\":\"gr\"}}}"
        (Blade.IdeServe.renderPlotResponse 1 "pdf" None "JVBERi0=")

    // A plotId is client-supplied text, so it gets the frame format's own
    // escape -- including the control character that would otherwise let a
    // payload forge a frame boundary.
    pin "renderPlot response: the meta id is JSON-escaped"
        "{\"id\":2,\"frame\":{\"v\":1,\"mime\":\"image/png\",\"encoding\":\"base64\",\"data\":\"AA==\",\"meta\":{\"id\":\"a\\\"b\\\\c\\u0001d\",\"backend\":\"gr\"}}}"
        (Blade.IdeServe.renderPlotResponse 2 "png" (Some "a\"b\\cd") "AA==")

    pin "mimeForFormat maps the three formats (and defaults to png)"
        "image/png|image/svg+xml|application/pdf|image/png"
        (String.concat "|" [ Blade.IdeServe.mimeForFormat "png"
                             Blade.IdeServe.mimeForFormat "svg"
                             Blade.IdeServe.mimeForFormat "pdf"
                             Blade.IdeServe.mimeForFormat "?" ])

    pin "dimensions clamp to [64..4096]"
        "64,64,800,4096,4096"
        (String.concat "," [ string (Blade.IdeServe.clampDim -5)
                             string (Blade.IdeServe.clampDim 63)
                             string (Blade.IdeServe.clampDim 800)
                             string (Blade.IdeServe.clampDim 4097)
                             string (Blade.IdeServe.clampDim 1000000) ])

    // ---- 2. Request rules, through the real loop --------------------------
    //
    // Every one of these fails BEFORE any worker is needed, so they hold on a
    // machine with no GR and no helper. The last one is the point of the
    // group: a bad render request is data, not an incident.

    let errorsFor (reqs: string list) = drive (reqs @ ["{\"cmd\":\"shutdown\"}"])

    (match errorsFor ["{\"id\":1,\"cmd\":\"renderPlot\"}"] with
     | [ r ] -> pin "renderPlot without a spec is a plain error response"
                    "{\"id\":1,\"error\":\"\\\"renderPlot\\\" requires a \\\"spec\\\" object\"}" r
     | other -> record "renderPlot without a spec is a plain error response" Fail (sprintf "%A" other))

    (match errorsFor ["{\"id\":1,\"cmd\":\"renderPlot\",\"spec\":\"not-an-object\"}"] with
     | [ r ] -> pin "a non-object spec is refused the same way"
                    "{\"id\":1,\"error\":\"\\\"renderPlot\\\" requires a \\\"spec\\\" object\"}" r
     | other -> record "a non-object spec is refused the same way" Fail (sprintf "%A" other))

    (match errorsFor ["{\"id\":9,\"cmd\":\"renderPlot\",\"spec\":{},\"height\":\"600\"}"] with
     | [ r ] -> pin "a non-integer dimension is an error, not a silent default"
                    "{\"id\":9,\"error\":\"\\\"height\\\" must be an integer\"}" r
     | other -> record "a non-integer dimension is an error, not a silent default" Fail (sprintf "%A" other))

    (match errorsFor ["{\"id\":9,\"cmd\":\"renderPlot\",\"spec\":{},\"format\":\"gif\"}"] with
     | [ r ] -> pin "an unsupported format names the three that work"
                    "{\"id\":9,\"error\":\"unknown format 'gif' (expected \\\"png\\\", \\\"svg\\\" or \\\"pdf\\\")\"}" r
     | other -> record "an unsupported format names the three that work" Fail (sprintf "%A" other))

    (match errorsFor ["{\"cmd\":\"renderPlot\",\"spec\":{}}"; "{\"id\":4,\"cmd\":\"ping\"}"] with
     | [ bad; pong ] ->
         ok "an id-less renderPlot errors and the loop takes the next request"
            (bad.Contains "\"id\":null" && bad.Contains "requires an integer" && pong.Contains "\"id\":4")
            (sprintf "%s / %s" bad pong)
     | other -> record "an id-less renderPlot errors and the loop takes the next request" Fail (sprintf "%A" other))

    // ---- 3. Resolution --------------------------------------------------
    //
    // Both GR failure modes are pre-validated rather than spawned into: without
    // GRDIR the GR call is an access violation, without %GRDIR%\bin on PATH it
    // is a DLL-not-found -- both SILENT (plan section 7).

    let tmpDir = Path.Combine(Path.GetTempPath(), "blade_grrender_" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory tmpDir |> ignore
    let grRoot = Path.Combine(tmpDir, "grfake")
    Directory.CreateDirectory (Path.Combine(grRoot, "bin")) |> ignore
    let grRootNoBin = Path.Combine(tmpDir, "grfake-nobin")
    Directory.CreateDirectory grRootNoBin |> ignore
    // Whatever the machine really has, captured BEFORE any fake is installed --
    // the gated end-to-end case below is judged against this, not against the
    // environment the fake tests build.
    let realAvailability = GR.availability ()

    let reasonOf (a: GR.Availability) =
        match a with
        | GR.Available p -> "available: " + p
        | GR.Unavailable r -> r

    withEnv [ "BLADE_GR_RENDER", Some (Path.Combine(tmpDir, "no-such-helper.exe"))
              "GRDIR", Some grRoot ] (fun () ->
        let r = reasonOf (GR.availability ())
        ok "an explicit BLADE_GR_RENDER that does not exist is an error, not a fallthrough"
           (r.StartsWith "gr-render helper not found: BLADE_GR_RENDER points at" && r.EndsWith "does not exist")
           r)

    // A helper that DOES exist, so the GR half of resolution is what answers.
    let helperStub = Path.Combine(tmpDir, "stub.ps1")
    File.WriteAllText(helperStub, "exit 0\n")

    withEnv [ "BLADE_GR_RENDER", Some helperStub; "GRDIR", None ] (fun () ->
        pin "GRDIR unset is reported in words, never spawned into"
            "GR unavailable: GRDIR not set" (reasonOf (GR.availability ())))

    withEnv [ "BLADE_GR_RENDER", Some helperStub; "GRDIR", Some grRootNoBin ] (fun () ->
        let r = reasonOf (GR.availability ())
        ok "a GRDIR without a bin/ is reported in words too"
           (r.StartsWith "GR unavailable: GRDIR bin missing" && r.Contains "bin")
           r)

    withEnv [ "BLADE_GR_RENDER", Some helperStub; "GRDIR", Some grRoot ] (fun () ->
        match GR.availability () with
        | GR.Available p -> pin "a resolvable helper plus a real GRDIR is Available" helperStub p
        | GR.Unavailable r -> record "a resolvable helper plus a real GRDIR is Available" Fail r)

    // ---- 3b. Platform-stamped resolution -----------------------------------
    //
    // `resolveHelperFrom` is `resolveHelper` with `baseDir` factored out
    // (GrRender.fs), so these drive the beside/walk-up search over a
    // disposable directory tree instead of the real Blade.exe location --
    // proof that a `gr-render-<platform>-<arch>[.exe]` prebuilt is found at
    // all, and PREFERRED over a plain `gr-render.exe` at the same location,
    // without needing to fake where Blade.exe itself runs from.
    //
    // BLADE_GR_RENDER is forced unset for all of them: resolveHelperFrom's
    // first branch returns unconditionally once it's set, so leaving it on
    // (from an earlier test, or the ambient dev environment) would silently
    // skip every case below.

    withEnv [ "BLADE_GR_RENDER", None ] (fun () ->
        let resDir = Path.Combine(tmpDir, "resolve")
        Directory.CreateDirectory resDir |> ignore

        // (a) only the plain name beside baseDir -> that's what's found.
        let besideOnlyPlain = Path.Combine(resDir, "beside-plain")
        Directory.CreateDirectory besideOnlyPlain |> ignore
        let plainHere = Path.Combine(besideOnlyPlain, "gr-render.exe")
        File.WriteAllText(plainHere, "x")
        match GR.resolveHelperFrom besideOnlyPlain with
        | Ok p -> pin "beside baseDir: the plain name is found when no stamped build exists" plainHere p
        | Error e -> record "beside baseDir: the plain name is found when no stamped build exists" Fail e

        // (b) both names beside baseDir -> the platform-stamped one wins.
        let besideBoth = Path.Combine(resDir, "beside-both")
        Directory.CreateDirectory besideBoth |> ignore
        File.WriteAllText(Path.Combine(besideBoth, "gr-render.exe"), "x")
        let stampedHere = Path.Combine(besideBoth, GR.stampedHelperLeaf)
        File.WriteAllText(stampedHere, "x")
        match GR.resolveHelperFrom besideBoth with
        | Ok p -> pin "beside baseDir: a platform-stamped build is preferred over the plain name" stampedHere p
        | Error e -> record "beside baseDir: a platform-stamped build is preferred over the plain name" Fail e

        // (c) walking up: nothing beside baseDir, but tools/gr-render two
        // levels above it holds both names -- the same stamped-over-plain
        // preference applies there too.
        let walkRoot = Path.Combine(resDir, "walk-root")
        let walkBase = Path.Combine(walkRoot, "a", "b")
        Directory.CreateDirectory walkBase |> ignore
        let toolsDir = Path.Combine(walkRoot, "tools", "gr-render")
        Directory.CreateDirectory toolsDir |> ignore
        File.WriteAllText(Path.Combine(toolsDir, "gr-render.exe"), "x")
        let stampedUp = Path.Combine(toolsDir, GR.stampedHelperLeaf)
        File.WriteAllText(stampedUp, "x")
        match GR.resolveHelperFrom walkBase with
        | Ok p -> pin "walking up: a platform-stamped build in tools/gr-render is preferred too" stampedUp p
        | Error e -> record "walking up: a platform-stamped build in tools/gr-render is preferred too" Fail e

        // (d) neither name anywhere -> the error names both, so a user reading
        // it knows a stamped prebuilt would also have satisfied it.
        let emptyBase = Path.Combine(resDir, "empty")
        Directory.CreateDirectory emptyBase |> ignore
        match GR.resolveHelperFrom emptyBase with
        | Error e ->
            ok "not found anywhere names both the stamped and plain leaf"
               (e.Contains GR.stampedHelperLeaf && e.Contains "gr-render.exe" && e.Contains " or ")
               e
        | Ok p -> record "not found anywhere names both the stamped and plain leaf" Fail (sprintf "unexpectedly resolved: %s" p))

    // ---- 4. The worker protocol, against a fake helper ---------------------

    let logPath = Path.Combine(tmpDir, "request.log")
    let envPath = Path.Combine(tmpDir, "child-env.txt")
    let markerPath = Path.Combine(tmpDir, "died-once.marker")
    let installFake (mode: string) =
        let p = Path.Combine(tmpDir, mode + ".ps1")
        File.WriteAllText(p, fakeScript mode logPath envPath markerPath)
        p
    /// One render against the named fake, with the environment the worker
    /// needs. `GR_DISPLAY` is SET here on purpose: the child must not see it.
    let withFake (mode: string) (f: unit -> unit) =
        withEnv [ "BLADE_GR_RENDER", Some (installFake mode)
                  "GRDIR", Some grRoot
                  "GR_DISPLAY", Some "should-be-removed" ] (fun () ->
            try f () finally GR.shutdown ())
    let workerTests = [
        "the worker answers a render with base64 payload bytes"
        "the request line splices the spec VERBATIM"
        "the worker's environment carries GRDIR, GKS_WSTYPE and the GR bin on PATH"
        "GR_DISPLAY is removed from the worker's environment"
        "an ok:false answer is a render error and LEAVES THE WORKER UP"
        "a malformed answer is an error and takes the worker DOWN"
        "a worker that dies mid-request fails that render and respawns on the next"
        "a render that never answers times out and takes the worker down"
        "renderPlot end to end: the loop answers with the frame the worker drew" ]

    if not (OperatingSystem.IsWindows()) then
        for name in workerTests do
            record name Skip "the fake helper is a PowerShell script (Windows only)"
    else
        // A spec whose numbers a reparse-and-reserialize round trip would
        // rewrite: 1.500 loses its zeros, 1e2 becomes 100, -0.0 loses its sign.
        // The request line must carry them exactly as they arrived.
        let spec = "{\"data\":[{\"type\":\"scatter\",\"x\":[1.500,1e2,-0.0],\"y\":[0,1,2]}],\"layout\":{}}"

        withFake "ok" (fun () ->
            match GR.render spec 640 480 "png" with
            | Ok data -> pin "the worker answers a render with base64 payload bytes" "QUJD" data
            | Error e -> record "the worker answers a render with base64 payload bytes" Fail e
            let sent = if File.Exists logPath then File.ReadAllText logPath else "<no request recorded>"
            // The leading id is the worker's own request counter, which is
            // process-lifetime state and not this test's business (the worker
            // echoing it back is what the render already depends on). Pin
            // everything after it -- which is where the spec is.
            let tail =
                let i = sent.IndexOf ",\"cmd\""
                if i < 0 then sent else sent.Substring i
            let expected =
                ",\"cmd\":\"render\",\"spec\":" + spec + ",\"width\":640,\"height\":480,\"format\":\"png\"}"
            pin "the request line splices the spec VERBATIM" expected tail
            let childEnv = if File.Exists envPath then File.ReadAllText envPath else "<no env recorded>"
            ok "the worker's environment carries GRDIR, GKS_WSTYPE and the GR bin on PATH"
               (childEnv.Contains ("GRDIR=" + grRoot)
                && childEnv.Contains "WSTYPE=100"
                && childEnv.Contains ("PATH0=" + Path.Combine(grRoot, "bin")))
               childEnv
            ok "GR_DISPLAY is removed from the worker's environment"
               (childEnv.Contains "DISPLAY=[]") childEnv)

        withFake "fail" (fun () ->
            match GR.render spec 800 600 "png" with
            | Ok data -> record "an ok:false answer is a render error and LEAVES THE WORKER UP" Fail ("rendered " + data)
            | Error e ->
                ok "an ok:false answer is a render error and LEAVES THE WORKER UP"
                   (e = "render failed: contourf: unsupported trace" && GR.workerAlive ())
                   (sprintf "%s (alive=%b)" e (GR.workerAlive ())))

        withFake "garbage" (fun () ->
            match GR.render spec 800 600 "png" with
            | Ok data -> record "a malformed answer is an error and takes the worker DOWN" Fail ("rendered " + data)
            | Error e ->
                ok "a malformed answer is an error and takes the worker DOWN"
                   (e.StartsWith "gr-render worker sent a malformed response" && not (GR.workerAlive ()))
                   (sprintf "%s (alive=%b)" e (GR.workerAlive ())))

        withFake "dieonce" (fun () ->
            let first = GR.render spec 800 600 "png"
            let second = GR.render spec 800 600 "png"
            match first, second with
            | Error e, Ok "QUJD" ->
                ok "a worker that dies mid-request fails that render and respawns on the next"
                   (e.Contains "gr-render worker") e
            | f, s ->
                record "a worker that dies mid-request fails that render and respawns on the next"
                       Fail (sprintf "first=%A second=%A" f s))

        withFake "slow" (fun () ->
            let saved = GR.renderTimeoutMs
            try
                GR.renderTimeoutMs <- 400
                match GR.render spec 800 600 "png" with
                | Ok data -> record "a render that never answers times out and takes the worker down" Fail ("rendered " + data)
                | Error e ->
                    ok "a render that never answers times out and takes the worker down"
                       (e = "gr-render worker timed out after 400 ms" && not (GR.workerAlive ()))
                       (sprintf "%s (alive=%b)" e (GR.workerAlive ()))
            finally GR.renderTimeoutMs <- saved)

        // The whole lane, through the real loop: request in, display frame out.
        withFake "ok" (fun () ->
            let req =
                "{\"id\":5,\"cmd\":\"renderPlot\",\"spec\":" + spec
                + ",\"plotId\":\"blade-2\",\"width\":320,\"height\":240}"
            match drive [ req; "{\"cmd\":\"shutdown\"}" ] with
            | [ r ] ->
                pin "renderPlot end to end: the loop answers with the frame the worker drew"
                    "{\"id\":5,\"frame\":{\"v\":1,\"mime\":\"image/png\",\"encoding\":\"base64\",\"data\":\"QUJD\",\"meta\":{\"id\":\"blade-2\",\"backend\":\"gr\"}}}"
                    r
            | other ->
                record "renderPlot end to end: the loop answers with the frame the worker drew"
                       Fail (sprintf "%A" other))

    // ---- 5. The gated end-to-end case -------------------------------------
    //
    // Skips like the g++-gated blocks do, and for the same reason: the resource
    // is not something the build produces. It goes live the moment a real
    // gr-render.exe and a GR install are both resolvable.

    let e2e = "a real gr-render renders a scatter spec to a PNG"
    (match realAvailability with
     | GR.Unavailable reason -> record e2e Skip reason
     | GR.Available helper ->
         let spec =
             "{\"data\":[{\"type\":\"scatter\",\"x\":[0.0,1.0,2.0],\"y\":[0.0,1.0,4.0],\"mode\":\"markers\"}],\
              \"layout\":{\"title\":{\"text\":\"gated\"}}}"
         let req =
             "{\"id\":1,\"cmd\":\"renderPlot\",\"spec\":" + spec
             + ",\"plotId\":\"gr-e2e\",\"width\":320,\"height\":240,\"format\":\"png\"}"
         match drive [ req; "{\"cmd\":\"shutdown\"}" ] with
         | [ r ] when r.Contains "\"frame\"" ->
             use doc = System.Text.Json.JsonDocument.Parse r
             let frame = doc.RootElement.GetProperty "frame"
             let data = (frame.GetProperty "data").GetString()
             let bytes = try Convert.FromBase64String data with _ -> [||]
             let png = [| 0x89uy; 0x50uy; 0x4Euy; 0x47uy; 0x0Duy; 0x0Auy; 0x1Auy; 0x0Auy |]
             let sig8 = if bytes.Length >= 8 then Array.sub bytes 0 8 else [||]
             ok e2e
                ((frame.GetProperty "mime").GetString() = "image/png"
                 && (frame.GetProperty("meta").GetProperty "id").GetString() = "gr-e2e"
                 && sig8 = png)
                (sprintf "%s: %d bytes, leading %A" helper bytes.Length sig8)
         | other -> record e2e Fail (sprintf "%A" other))

    (try GR.shutdown () with _ -> ())
    (try Directory.Delete(tmpDir, true) with _ -> ())

    let count o = results |> Seq.filter (fun (_, r) -> r = o) |> Seq.length
    let passed, failed, skipped = count Pass, count Fail, count Skip
    let failedNames = results |> Seq.filter (fun (_, r) -> r = Fail) |> Seq.map fst |> List.ofSeq
    let parts =
        [ sprintf "%d passed" passed; sprintf "%d failed" failed ]
        @ (if skipped > 0 then [ sprintf "%d skipped" skipped ] else [])
    printFooter blockName parts
    { Block = blockName; Passed = passed; Failed = failed; Skipped = skipped; FailedNames = failedNames }
