// Icechunk provider tests. Fully hermetic: no real Icechunk repo is committed
// and none is needed. Sections 1-8 are PURE -- assertions about functions whose
// inputs are bytes, strings or synthetic domain values, plus a handful of
// hand-assembled 39-byte headers in a temp directory, which is enough to
// exercise every refusal that fires before a payload is even looked at.
//
// Sections 9-11 add the other half: REAL spec-2 repos, written on the fly by
// `Blade.IcechunkWrite` (src/providers/IcechunkWrite.fs) into
// tests/fixtures/icechunk_repos/ -- the ZarrWrite discipline, applied to a
// versioned store. Section 9 pins the WRITER without the provider in the loop
// (layout, byte-level headers, base32 file-name shapes, id determinism and
// reuse); section 10 drives the PROVIDER'S public surface against those repos
// (ref resolution, checkout modules, dense reads, every named refusal);
// section 11 compiles and runs a Blade program that reads one, behind the
// `Build.compileCpp` / `isSkipError` skip discipline ZarrTests uses.
//
// The reader and the writer state the format INDEPENDENTLY -- IcechunkWrite
// re-derives the magic bytes, the header layout and the Crockford encoder
// rather than calling the provider's -- so a round-trip through both is a real
// cross-check and not a tautology.
module Blade.Tests.IcechunkTests

open System
open System.IO
open Blade
open Blade.IR
open Blade.Types
open Blade.Lowering
open Blade.Build
open Blade.IcechunkProvider
open Blade.Tests.TestHarness

/// The fixture writer, always qualified: it deliberately shares NAMES with the
/// provider (`base32Encode`, `magicBytes`, `headerSize`, `repoFilePath`), and
/// the point of these tests is that the two agree -- which they cannot do if
/// an `open` silently picks one.
module IW = Blade.IcechunkWrite

let runIcechunkTests () =
    printHeader "Icechunk Provider Tests"
    let mutable passed = 0
    let mutable failed = 0

    let check (name: string) (condition: bool) (detail: string) =
        if condition then
            printfn "  PASS: %s" name
            passed <- passed + 1
        else
            printfn "  FAIL: %s — %s" name detail
            failed <- failed + 1

    let isError (r: Result<'a, string>) (needle: string) =
        match r with
        | Error e -> e.Contains needle
        | Ok _ -> false

    let errorText (r: Result<'a, string>) =
        match r with
        | Error e -> e
        | Ok _ -> "<Ok>"

    /// The message an exception-throwing entry point produced ("<no
    /// exception>" when it did not throw), so failures print what happened.
    let caught (f: unit -> 'a) : string =
        try
            f () |> ignore
            "<no exception>"
        with ex -> ex.Message

    // "Check the environment FIRST." Probed once, here, so the e2e section
    // can tell a genuine missing-toolchain SKIP from a real failure.
    let icCaps = Blade.Build.capabilities.Value

    /// Verdict for a failing BASELINE build (the reference compile+run every
    /// later assertion in a block depends on). A missing toolchain is a SKIP;
    /// a lowering error, a compile failure or a nonzero exit is a FAILURE.
    /// Printing SKIP unconditionally here would silently DELETE the
    /// assertions that ride on the baseline -- the block would stay green
    /// with nothing tested (the trap ZarrTests documents at its own top).
    let baselineFailed (what: string) (e: string) =
        if not icCaps.HasGpp then printfn "  SKIP %s: g++ not found (%s)" what e
        elif isSkipError e then printfn "  SKIP %s: %s" what e
        else check ($"{what}: baseline builds and runs") false e

    // Fixture repos live under tests/fixtures/icechunk_repos/ (see the README
    // there). The SAME relative string resolves at the compiler cwd
    // (compile-time ref resolution and metadata loads) and, mirrored under
    // generated_cpp_tests, at the exe cwd (runtime chunk reads).
    let fixRepo (name: string) = "tests/fixtures/icechunk_repos/" + name

    // ---------------------------------------------------------------
    // 1. The 39-byte metadata header (golden bytes)
    // ---------------------------------------------------------------
    printfn "\n--- metadata header ---"

    // The magic spelled out BYTE BY BYTE: UTF-8 of "ICE" + U+1F9CA + "CHUNK".
    // Written as bytes on purpose -- the point of the test is that the
    // parser's 12-byte magic is these bytes, not that some string literal
    // happens to have length 12 in whatever encoding the source was saved in.
    let goldenMagic =
        [| 0x49uy; 0x43uy; 0x45uy                     // I C E
           0xF0uy; 0x9Fuy; 0xA7uy; 0x8Auy             // U+1F9CA
           0x43uy; 0x48uy; 0x55uy; 0x4Euy; 0x4Buy |]  // C H U N K

    check "header: magic is 12 bytes" (goldenMagic.Length = 12) $"{goldenMagic.Length}"
    check "header: provider magic matches the golden bytes"
        (magicBytes = goldenMagic) (magicBytes |> Array.map (sprintf "%02x") |> String.concat " ")
    check "header: framing is 39 bytes" (headerSize = 39) $"{headerSize}"

    /// Build a 39-byte header: 12 magic + 24 space-padded impl name + spec + type + compression.
    let mkHeader (impl: string) (specByte: byte) (typeByte: byte) (compByte: byte) : byte[] =
        let implField = Array.create 24 0x20uy
        let implBytes = Text.Encoding.UTF8.GetBytes impl
        Array.blit implBytes 0 implField 0 (min implBytes.Length 24)
        Array.concat [ goldenMagic; implField; [| specByte; typeByte; compByte |] ]

    check "header: constructed header is exactly 39 bytes"
        ((mkHeader "icechunk-rust" 2uy 6uy 1uy).Length = 39) ""

    (match parseHeader "test" (mkHeader "icechunk-rust 1.2.3" 2uy 6uy 1uy) with
     | Ok h ->
         check "header: implementation name unpads" (h.Implementation = "icechunk-rust 1.2.3") h.Implementation
         check "header: spec version 2 accepted" (h.SpecVersion = 2) $"{h.SpecVersion}"
         check "header: file type 6 = RepoInfo" (h.FileType = FtRepoInfo) (fileTypeName h.FileType)
         check "header: compression 1 = zstd" (h.Compression = CompZstd) (compressionName h.Compression)
     | Error e -> check "header: a well-formed spec-2 header parses" false e)

    (match parseHeader "test" (mkHeader "x" 2uy 1uy 0uy) with
     | Ok h ->
         check "header: file type 1 = Snapshot" (h.FileType = FtSnapshot) (fileTypeName h.FileType)
         check "header: compression 0 = none" (h.Compression = CompNone) (compressionName h.Compression)
     | Error e -> check "header: snapshot header parses" false e)

    check "header: file type 2 = Manifest"
        (match parseHeader "test" (mkHeader "x" 2uy 2uy 0uy) with
         | Ok h -> h.FileType = FtManifest
         | Error _ -> false) ""
    check "header: file type 4 = TransactionLog"
        (match parseHeader "test" (mkHeader "x" 2uy 4uy 0uy) with
         | Ok h -> h.FileType = FtTransactionLog
         | Error _ -> false) ""
    // Unstamped-but-defined enum members are TOLERATED (they parse, and the
    // diagnostic names them) rather than refused at the header.
    check "header: file type 3 = Attributes (tolerated)"
        (match parseHeader "test" (mkHeader "x" 2uy 3uy 0uy) with
         | Ok h -> h.FileType = FtAttributes
         | Error _ -> false) ""
    check "header: file type 5 = Chunk (tolerated)"
        (match parseHeader "test" (mkHeader "x" 2uy 5uy 0uy) with
         | Ok h -> h.FileType = FtChunk
         | Error _ -> false) ""
    check "header: an unknown file type parses and is NAMED, not swallowed"
        (match parseHeader "test" (mkHeader "x" 2uy 99uy 0uy) with
         | Ok h -> h.FileType = FtUnknown 99 && (fileTypeName h.FileType).Contains "99"
         | Error _ -> false) ""

    // Spec version 1: refused BY NAME, citing the version this reader wants.
    (let r = parseHeader "repo file 'r/repo'" (mkHeader "icechunk-rust" 1uy 6uy 0uy)
     check "header: spec version 1 refused by name"
         (isError r "spec version 1" && isError r "spec version 2") (errorText r))
    (let r = parseHeader "test" (mkHeader "icechunk-rust" 3uy 6uy 0uy)
     check "header: an unknown spec version refuses as unknown"
         (isError r "unknown Icechunk spec version 3") (errorText r))

    // Compression: 0 and 1 known, anything else refused (never assumed raw).
    (let r = parseHeader "test" (mkHeader "x" 2uy 6uy 7uy)
     check "header: unknown compression byte refused"
         (isError r "unknown compression byte 7") (errorText r))

    (let r = parseHeader "test" (Array.sub (mkHeader "x" 2uy 6uy 0uy) 0 38)
     check "header: a 38-byte file is refused as too short"
         (isError r "39-byte") (errorText r))
    (let r = parseHeader "test" [||]
     check "header: an empty file is refused as too short" (isError r "39-byte") (errorText r))

    (let bad = mkHeader "x" 2uy 6uy 0uy
     bad.[3] <- 0x00uy
     let r = parseHeader "test" bad
     check "header: wrong magic refuses as 'not an Icechunk metadata file'"
         (isError r "not an Icechunk metadata file") (errorText r))

    // The WRITER's header is the same 39 bytes, derived independently.
    (let written = IW.makeHeader "blade-fixtures" 2uy IW.ftRepoInfo IW.compZstd
     check "header: the fixture writer's header is byte-identical to the twin"
         (written = mkHeader "blade-fixtures" 2uy 6uy 1uy)
         (written |> Array.map (sprintf "%02x") |> String.concat " ")
     check "header: the writer's magic equals the provider's"
         (IW.magicBytes = magicBytes) ""
     check "header: the writer's framing size equals the provider's"
         (IW.headerSize = headerSize) $"{IW.headerSize}"
     match parseHeader "written" written with
     | Ok h ->
         check "header: the provider parses what the writer wrote"
             (h.Implementation = "blade-fixtures" && h.SpecVersion = 2
              && h.FileType = FtRepoInfo && h.Compression = CompZstd) $"%A{h}"
     | Error e -> check "header: the provider parses what the writer wrote" false e)
    check "header: the writer refuses an over-long implementation name"
        ((caught (fun () -> IW.makeHeader (String.replicate 25 "x") 2uy 6uy 0uy)).Contains "header field")
        (caught (fun () -> IW.makeHeader (String.replicate 25 "x") 2uy 6uy 0uy))

    // ---------------------------------------------------------------
    // 2. Crockford base32 (object ids -> file names)
    // ---------------------------------------------------------------
    printfn "\n--- Crockford base32 ---"

    check "base32: alphabet is 32 characters" (base32Alphabet.Length = 32) base32Alphabet
    check "base32: alphabet omits I, L, O and U"
        (["I"; "L"; "O"; "U"] |> List.forall (fun c -> not (base32Alphabet.Contains c))) base32Alphabet

    // The spec's own example id.
    (let bytes =
        [| 0x0buy; 0x1cuy; 0xc8uy; 0xd6uy; 0x78uy; 0x75uy; 0x80uy; 0xf0uy; 0xe3uy; 0x3auy; 0x65uy; 0x34uy |]
     let s = base32Encode bytes
     check "base32: spec example encodes to 1CECHNKREP0F1RSTCMT0" (s = "1CECHNKREP0F1RSTCMT0") s
     check "base32: the fixture writer's encoder agrees on the spec example"
         (IW.base32Encode bytes = s) (IW.base32Encode bytes))

    let zeros12 = base32Encode (Array.zeroCreate 12)
    let zeros8 = base32Encode (Array.zeroCreate 8)
    check "base32: a 12-byte object id is 20 characters" (zeros12.Length = objectIdChars) zeros12
    check "base32: an 8-byte node id is 13 characters" (zeros8.Length = nodeIdChars) zeros8
    check "base32: all-zero bytes encode to all zeros (right-padded, no '=')"
        (zeros12 = String.replicate 20 "0") zeros12
    // 64 one-bits: 12 full groups of 11111, then 1111 right-padded to 11110.
    check "base32: 8 x 0xFF encodes to ZZZZZZZZZZZZY (zero-bit right padding)"
        (base32Encode (Array.create 8 0xFFuy) = "ZZZZZZZZZZZZY") (base32Encode (Array.create 8 0xFFuy))
    check "base32: empty input encodes to the empty string" (base32Encode [||] = "") (base32Encode [||])

    // The two encoders are independent statements of the same rule; a
    // disagreement anywhere in the byte space would make every fixture file
    // name unreadable by the provider, so sweep rather than spot-check.
    check "base32: writer and provider encoders agree over 256 12-byte ids"
        ([ 0 .. 255 ] |> List.forall (fun i ->
            let b = Array.init 12 (fun j -> byte ((i * 31 + j * 7) % 256))
            IW.base32Encode b = base32Encode b)) ""

    check "base32: a 20-char canonical id is object-id shaped"
        (isObjectIdForm "1CECHNKREP0F1RSTCMT0") ""
    check "base32: 19 characters is NOT object-id shaped" (not (isObjectIdForm "1CECHNKREP0F1RSTCMT")) ""
    check "base32: a name containing 'I' is NOT object-id shaped (I is not in the alphabet)"
        (not (isObjectIdForm "1CECHNKREPIF1RSTCMT0")) ""
    check "base32: lowercase is NOT object-id shaped (ids are canonical uppercase)"
        (not (isObjectIdForm "1cechnkrep0f1rstcmt0")) ""
    check "base32: a plain branch name is not mistaken for a snapshot id"
        (not (isObjectIdForm "main")) ""

    // ---------------------------------------------------------------
    // 3. The canonical key (plan §3.1)
    // ---------------------------------------------------------------
    printfn "\n--- canonical key ---"

    let roundTrips (k: string) =
        match parseKey k with
        | Ok parsed -> formatKey parsed = k
        | Error _ -> false

    (match parseKey "data/weather.icechunk" with
     | Ok k ->
         check "key: a bare path is a repo-handle load" (k.Ref = None && k.RepoPath = "data/weather.icechunk") $"%A{k}"
     | Error e -> check "key: bare path parses" false e)

    (match parseKey "data/weather.icechunk@branch:main" with
     | Ok k ->
         check "key: branch refspec parses"
             (k.RepoPath = "data/weather.icechunk" && k.Ref = Some (RefBranch, "main")) $"%A{k}"
     | Error e -> check "key: branch key parses" false e)

    (match parseKey "r@tag:v1.0" with
     | Ok k -> check "key: tag refspec parses" (k.Ref = Some (RefTag, "v1.0")) $"%A{k}"
     | Error e -> check "key: tag key parses" false e)
    (match parseKey "r@snapshot:1CECHNKREP0F1RSTCMT0" with
     | Ok k ->
         check "key: snapshot refspec parses"
             (k.Ref = Some (RefSnapshot, "1CECHNKREP0F1RSTCMT0")) $"%A{k}"
     | Error e -> check "key: snapshot key parses" false e)
    (match parseKey "r@?:main" with
     | Ok k -> check "key: '?' is the bare checkout form" (k.Ref = Some (RefBare, "main")) $"%A{k}"
     | Error e -> check "key: bare-form key parses" false e)

    // A Windows path's drive colon must not be mistaken for the kind:name colon.
    (match parseKey "C:\\data\\w.icechunk@branch:main" with
     | Ok k ->
         check "key: a Windows drive path survives parsing"
             (k.RepoPath = "C:\\data\\w.icechunk" && k.Ref = Some (RefBranch, "main")) $"%A{k}"
     | Error e -> check "key: Windows path key parses" false e)
    (match parseKey "C:\\data\\w.icechunk" with
     | Ok k -> check "key: a bare Windows path is a repo handle" (k.Ref = None) $"%A{k}"
     | Error e -> check "key: bare Windows path parses" false e)

    check "key: round-trips (bare)" (roundTrips "data/weather.icechunk") ""
    check "key: round-trips (branch)" (roundTrips "data/weather.icechunk@branch:main") ""
    check "key: round-trips (tag)" (roundTrips "data/weather.icechunk@tag:v1.0") ""
    check "key: round-trips (snapshot)" (roundTrips "r@snapshot:1CECHNKREP0F1RSTCMT0") ""
    check "key: round-trips (bare form '?')" (roundTrips "r@?:main") ""
    check "key: kind tokens are branch/tag/snapshot/?"
        (List.map kindToken [RefBranch; RefTag; RefSnapshot; RefBare] = ["branch"; "tag"; "snapshot"; "?"]) ""

    // The desugar and the provider must spell the key the same way, or a
    // checkout resolves to a path the provider parses differently.
    check "key: ProviderDesugar.canonicalKey is what parseKey accepts"
        (roundTrips (Blade.ProviderDesugar.canonicalKey "data/w.icechunk" "tag" "v1.0")
         && Blade.ProviderDesugar.canonicalKey "data/w.icechunk" "tag" "v1.0" = "data/w.icechunk@tag:v1.0")
        (Blade.ProviderDesugar.canonicalKey "data/w.icechunk" "tag" "v1.0")
    check "key: the desugar's bare-form kind token is the provider's '?'"
        (roundTrips (Blade.ProviderDesugar.canonicalKey "r" "?" "main")) ""

    (let r = parseKey "repo@main"
     check "key: a refspec with no ':' is refused loudly"
         (isError r "malformed store key" && isError r "branch, tag, snapshot") (errorText r))
    (let r = parseKey "repo@bogus:x"
     check "key: an unknown ref kind is refused loudly" (isError r "unknown ref kind 'bogus'") (errorText r))
    (let r = parseKey "repo@branch:"
     check "key: an empty ref name is refused loudly" (isError r "ref name after ':' is empty") (errorText r))
    (let r = parseKey "@branch:main"
     check "key: an empty repo path is refused loudly" (isError r "repo path before '@' is empty") (errorText r))
    (let r = parseKey "   "
     check "key: an empty key is refused loudly" (isError r "empty store path") (errorText r))

    (let r = checkLocalPath "s3://bucket/weather.icechunk"
     check "key: object-store URLs are refused BY NAME"
         (isError r "object-store URLs" && isError r "s3://") (errorText r))
    check "key: a local path passes the object-store gate"
        (match checkLocalPath "data/weather.icechunk" with Ok () -> true | Error _ -> false) ""

    // ---------------------------------------------------------------
    // 4. Ref resolution (pure, over synthetic repo files)
    // ---------------------------------------------------------------
    printfn "\n--- ref resolution ---"

    let snapOf (b: byte) : SnapshotInfo =
        { Id = Array.create 12 b; ParentOffset = 0; FlushedAtMillis = 0L; Message = "" }
    let snap1 = snapOf 0x11uy
    let snap2 = snapOf 0x22uy

    let info : RepoInfo = {
        Branches = [("main", 0); ("dev", 1)]
        Tags = [("v1.0", 0)]
        DeletedTags = ["old"]
        Snapshots = [snap1; snap2]
        Status = StatusOnline
    }

    check "resolve: branch by marker"
        (resolveRef info RefBranch "main" = Ok snap1.Id) (errorText (resolveRef info RefBranch "main"))
    check "resolve: the second branch resolves to the second snapshot"
        (resolveRef info RefBranch "dev" = Ok snap2.Id) (errorText (resolveRef info RefBranch "dev"))
    check "resolve: tag by marker"
        (resolveRef info RefTag "v1.0" = Ok snap1.Id) (errorText (resolveRef info RefTag "v1.0"))
    check "resolve: bare name unique across namespaces"
        (resolveRef info RefBare "main" = Ok snap1.Id) (errorText (resolveRef info RefBare "main"))
    check "resolve: a bare tag name resolves too (no namespace precedence needed when unique)"
        (resolveRef info RefBare "v1.0" = Ok snap1.Id) (errorText (resolveRef info RefBare "v1.0"))

    // A bare name shaped like an object id is matched against snapshot ids.
    (let sid = base32Encode snap2.Id
     check "resolve: a bare object-id-shaped name resolves as a snapshot"
         (resolveRef info RefBare sid = Ok snap2.Id) (errorText (resolveRef info RefBare sid))
     check "resolve: the same id resolves under the snapshot marker"
         (resolveRef info RefSnapshot sid = Ok snap2.Id) (errorText (resolveRef info RefSnapshot sid)))

    (let r = resolveRef info RefSnapshot "main"
     check "resolve: a non-id name under ic.snapshot is refused by shape"
         (isError r "is not a snapshot id" && isError r "20 Crockford") (errorText r))
    (let r = resolveRef info RefSnapshot (String.replicate 20 "0")
     check "resolve: an id-shaped name that no snapshot carries is refused"
         (isError r "no snapshot") (errorText r))

    // Zero hits: the refusal LISTS the repo's actual branches and tags.
    (let r = resolveRef info RefBare "nope"
     check "resolve: zero hits lists the repo's branches and tags"
         (isError r "no branch, tag or snapshot named 'nope'"
          && isError r "main" && isError r "dev" && isError r "v1.0") (errorText r))
    (let r = resolveRef info RefBranch "nope"
     check "resolve: a missing branch lists the branches"
         (isError r "no branch named 'nope'" && isError r "main, dev") (errorText r))
    (let r = resolveRef info RefTag "nope"
     check "resolve: a missing tag lists the tags"
         (isError r "no tag named 'nope'" && isError r "v1.0") (errorText r))

    // Two hits: name BOTH namespaces, and point at the markers.
    (let ambiguous = { info with Branches = [("shared", 0)]; Tags = [("shared", 1)] }
     let r = resolveRef ambiguous RefBare "shared"
     check "resolve: an ambiguous bare ref refuses, naming both namespaces"
         (isError r "ambiguous" && isError r "branch" && isError r "tag") (errorText r)
     check "resolve: the ambiguity refusal offers the ic.branch / ic.tag markers"
         (isError r "ic.branch" && isError r "ic.tag") (errorText r)
     check "resolve: the marker form disambiguates a colliding name"
         (resolveRef ambiguous RefBranch "shared" = Ok snap1.Id
          && resolveRef ambiguous RefTag "shared" = Ok snap2.Id) "")

    // Deleted tags are tombstones, in both the bare and marker forms.
    (let r = resolveRef info RefBare "old"
     check "resolve: a deleted tag name names its tombstone (bare)"
         (isError r "DELETED TAG" && isError r "deleted_tags") (errorText r))
    (let r = resolveRef info RefTag "old"
     check "resolve: a deleted tag name names its tombstone (ic.tag)"
         (isError r "DELETED TAG") (errorText r))

    // Repo status: Offline refuses; ReadOnly and Online read.
    (let offline = { info with Status = StatusOffline }
     let r = resolveRef offline RefBranch "main"
     check "resolve: an Offline repo refuses every ref"
         (isError r "Offline") (errorText r)
     check "resolve: Offline refuses the bare form too"
         (isError (resolveRef offline RefBare "main") "Offline") "")
    (let ro = { info with Status = StatusReadOnly }
     check "resolve: a ReadOnly repo resolves normally"
         (resolveRef ro RefBranch "main" = Ok snap1.Id) (errorText (resolveRef ro RefBranch "main"))
     check "status names round-trip"
         (List.map statusName [StatusOnline; StatusReadOnly; StatusOffline] = ["Online"; "ReadOnly"; "Offline"]) "")

    // A ref pointing outside the snapshot list is a corrupt repo file, not a miss.
    (let broken = { info with Branches = [("bad", 7)] }
     let r = resolveRef broken RefBranch "bad"
     check "resolve: an out-of-range snapshot index is refused as inconsistent"
         (isError r "inconsistent" && isError r "index 7") (errorText r))

    // ---------------------------------------------------------------
    // 5. The payload codec: zstd + FlatBuffers
    // ---------------------------------------------------------------
    // STALE-PENDING-P1: this section used to pin `decodePayload` and
    // `decompress` as honest STUBS by their `pendingPayloadDecode` /
    // `pendingZstd` messages -- the §6.2 dependency decision was open, and
    // those pins were what P1 would change. §6.2 is DECIDED (ZstdSharp.Port +
    // vendored flatc accessors), so the pins are inverted: what used to be
    // "this seam refuses by name" is now "this seam round-trips", and the
    // only thing still refused is a payload that really is malformed.
    printfn "\n--- payload codec (zstd + FlatBuffers) ---"

    check "codec: compression 0 is the identity (payload passes through unchanged)"
        (decompressPayload CompNone [| 7uy; 8uy; 9uy |] = Ok [| 7uy; 8uy; 9uy |])
        (errorText (decompressPayload CompNone [| 7uy; 8uy; 9uy |]))

    // Round-trip through the WRITER's compressor and the PROVIDER's
    // decompressor: two sides of the §6.2 decision, meeting in the middle.
    (let plain = Array.init 4096 (fun i -> byte ((i * 37) % 251))
     let squeezed = IW.compressZstd plain
     check "codec: zstd compression actually compresses a repetitive payload"
         (squeezed.Length > 0 && squeezed.Length < plain.Length) $"{squeezed.Length} vs {plain.Length}"
     check "codec: the provider decompresses what the writer compressed"
         (decompress squeezed = Ok plain) (errorText (decompress squeezed))
     check "codec: compression 1 routes through the same zstd path"
         (decompressPayload CompZstd squeezed = Ok plain)
         (errorText (decompressPayload CompZstd squeezed)))
    (let empty : byte[] = [||]
     check "codec: an empty payload survives the zstd round trip"
         (decompress (IW.compressZstd empty) = Ok empty)
         (errorText (decompress (IW.compressZstd empty))))

    // Not a zstd frame: a LOUD refusal, never a silent pass-through of the
    // compressed bytes (which would hand the FlatBuffers decoder garbage and
    // fail somewhere far away from the cause).
    check "codec: non-zstd bytes are refused, not passed through"
        (match decompress [| 1uy; 2uy; 3uy; 4uy |] with
         | Error _ -> true
         | Ok bs -> bs <> [| 1uy; 2uy; 3uy; 4uy |]) "compressed bytes passed through unchanged"

    // A FlatBuffer that is not one: refused, and never as a successful decode
    // of an empty table.
    check "codec: garbage payload bytes do not decode as a RepoInfo"
        (match decodePayload FtRepoInfo [| 1uy; 2uy; 3uy; 4uy |] with
         | Error _ -> true
         | Ok _ -> false) "garbage decoded as a repo"
    check "codec: a truncated payload does not decode as a Snapshot"
        (match decodePayload FtSnapshot [| 0uy |] with
         | Error _ -> true
         | Ok _ -> false) "one byte decoded as a snapshot"

    // ---------------------------------------------------------------
    // 6. Node paths and chunk tables (pure)
    // ---------------------------------------------------------------
    printfn "\n--- nodes and chunk tables ---"

    check "node: a root-level array path yields its variable name"
        (nodeVarName "/temp" = Ok "temp") (errorText (nodeVarName "/temp"))
    (let r = nodeVarName "/group/temp"
     check "node: a nested group refuses BY NAME"
         (isError r "NESTED GROUP" && isError r "root-level arrays only") (errorText r))
    (let r = nodeVarName "/"
     check "node: the root group is not an array" (isError r "root group") (errorText r))
    (let r = nodeVarName "temp"
     check "node: a relative path is refused" (isError r "is not absolute") (errorText r))
    (let r = nodeVarName "/2temp"
     check "node: a non-identifier array name is refused"
         (isError r "not a valid Blade identifier") (errorText r))

    // The chunk table unions manifests into row-major grid order; a
    // coordinate no manifest covers reads as fill.
    let v3json = """{"zarr_format": 3, "node_type": "array", "shape": [6, 4], "data_type": "float32",
                     "chunk_grid": {"name": "regular", "configuration": {"chunk_shape": [3, 4]}},
                     "fill_value": 0, "codecs": [{"name": "bytes", "configuration": {"endian": "little"}}],
                     "dimension_names": ["t", "p"]}"""
    (match ZarrProvider.parseArrayMetaV3 "temp" "/repo" v3json with
     | Error e -> check "chunks: the v3 user_data JSON parses with the Zarr parser" false e
     | Ok meta ->
         check "chunks: the v3 user_data JSON parses with the Zarr parser" true ""
         let native = Native { ChunkId = [| 0xAAuy; 0xAAuy; 0xAAuy |]; Offset = 0L; Length = 48L }
         let m1 = { NodeId = Array.zeroCreate 8; Refs = [ { Index = [1L; 0L]; Loc = native } ] }
         (match buildChunkTable meta [m1] with
          | Ok table ->
              check "chunks: the table has one entry per chunk-grid cell" (table.Length = 2) $"{table.Length}"
              check "chunks: an uncovered coordinate reads as Fill" (table.[0] = Fill) (sprintf "%A" table.[0])
              check "chunks: a covered coordinate carries its native ref" (table.[1] = native) (sprintf "%A" table.[1])
          | Error e -> check "chunks: a single-manifest table builds" false e)
         // Overlap is a refusal: §2 requires non-overlapping ChunkIndexRanges.
         let m2 = { NodeId = Array.zeroCreate 8; Refs = [ { Index = [1L; 0L]; Loc = Inline [| 1uy |] } ] }
         (let r = buildChunkTable meta [m1; m2]
          check "chunks: overlapping manifests are refused"
              (isError r "OVERLAP") (errorText r))
         (let r = buildChunkTable meta [ { NodeId = Array.zeroCreate 8; Refs = [ { Index = [9L; 0L]; Loc = Fill } ] } ]
          check "chunks: a coordinate outside the grid is refused"
              (isError r "outside the chunk grid") (errorText r))
         (let r = buildChunkTable meta [ { NodeId = Array.zeroCreate 8; Refs = [ { Index = [0L]; Loc = Fill } ] } ]
          check "chunks: a wrong-rank chunk index is refused"
              (isError r "is rank 1" && isError r "rank 2") (errorText r)))

    check "chunks: virtual refs have a NAMED refusal (no ChunkLoc case exists for them)"
        ((virtualChunkRefused "temp" "s3://other/f.nc").Contains "VIRTUAL chunk refs are not supported")
        (virtualChunkRefused "temp" "s3://other/f.nc")

    // ---------------------------------------------------------------
    // 7. Repo-file gates (hand-assembled headers -- no real repo)
    // ---------------------------------------------------------------
    printfn "\n--- repo file gates ---"

    let tmp = Path.Combine(Path.GetTempPath(), "blade_ic_" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory tmp |> ignore

    /// A repo directory whose `repo` file carries `header` and a nonsense payload.
    let fakeRepo (name: string) (header: byte[]) : string =
        let root = Path.Combine(tmp, name)
        Directory.CreateDirectory root |> ignore
        File.WriteAllBytes(Path.Combine(root, "repo"), Array.append header [| 0uy; 1uy; 2uy; 3uy |])
        root

    let junkRepo = fakeRepo "junk.icechunk" (mkHeader "icechunk-rust 1.2.3" 2uy 6uy 0uy)
    let junkZstdRepo = fakeRepo "junkzstd.icechunk" (mkHeader "icechunk-rust 1.2.3" 2uy 6uy 1uy)
    let spec1Repo = fakeRepo "spec1.icechunk" (mkHeader "icechunk-rust 0.1.0" 1uy 6uy 0uy)
    let wrongTypeRepo = fakeRepo "wrongtype.icechunk" (mkHeader "icechunk-rust" 2uy 1uy 0uy)
    let badCompRepo = fakeRepo "badcomp.icechunk" (mkHeader "icechunk-rust" 2uy 6uy 9uy)
    let emptyDir = Path.Combine(tmp, "empty.icechunk")
    Directory.CreateDirectory emptyDir |> ignore

    check "repo: a well-formed repo header validates (header level, no payload)"
        (match validateRepoFile junkRepo with Ok _ -> true | Error _ -> false)
        (errorText (validateRepoFile junkRepo))
    (let r = validateRepoFile (Path.Combine(tmp, "nope.icechunk"))
     check "repo: a missing repo directory refuses by name" (isError r "does not exist") (errorText r))
    (let r = validateRepoFile emptyDir
     check "repo: a directory with no 'repo' file is not an Icechunk repo"
         (isError r "is not an Icechunk repo") (errorText r))
    (let r = validateRepoFile spec1Repo
     check "repo: a spec-1 repo file refuses at the load site" (isError r "spec version 1") (errorText r))
    (let r = validateRepoFile wrongTypeRepo
     check "repo: a repo file stamped Snapshot refuses as the wrong file type"
         (isError r "expected RepoInfo") (errorText r))
    (let r = validateRepoFile badCompRepo
     check "repo: an unknown compression byte refuses at the repo file"
         (isError r "unknown compression byte 9") (errorText r))
    (let r = validateRepoFile "s3://bucket/repo.icechunk"
     check "repo: an object-store URL refuses before any file IO"
         (isError r "object-store URLs") (errorText r))

    // STALE-PENDING-P1: these three used to assert that `load` "reaches the
    // payload seam" -- i.e. that the pending §6.2 message was the error a
    // structurally fine repo produced. With the decoder live, the SAME
    // fixtures (a valid header over four junk bytes) must now fail as what
    // they are: an undecodable payload. What is pinned is that the failure is
    // loud and is NOT a pending-dependency message.
    (let r = load junkRepo
     check "load: a valid header over a junk payload fails as a DECODE error"
         (match r with Error e -> not (e.Contains "pending") | Ok _ -> false) (errorText r))
    (let r = load junkZstdRepo
     check "load: a junk zstd payload fails as a DECOMPRESSION error, not a stub"
         (match r with Error e -> not (e.Contains "pending") | Ok _ -> false) (errorText r))
    (let r = load (junkRepo + "@branch:main")
     check "load: a canonical key over a junk payload fails the same way"
         (match r with Error e -> not (e.Contains "pending") | Ok _ -> false) (errorText r))
    (let r = load (junkRepo + "@bogus:main")
     check "load: a malformed key refuses before any file IO"
         (isError r "unknown ref kind") (errorText r))

    // STALE-PENDING-P1: `loadAsModule` on a checkout key used to be pinned by
    // the pending payload message; the live behavior it must have is section
    // 10's (a real checkout builds a dims/vars module). What survives here is
    // the pre-payload half: a spec-1 repo and a malformed key still die at
    // the load site with their own names.
    (let msg = caught (fun () -> loadAsModule (IRBuilder()) "wx" spec1Repo)
     check "module: a spec-1 repo fails at the load site, by name"
         (msg.Contains "spec version 1") msg)
    (let msg = caught (fun () -> loadAsModule (IRBuilder()) "ck" (junkRepo + "@bogus:v1"))
     check "module: a malformed key fails loudly" (msg.Contains "malformed store key") msg)
    (let msg = caught (fun () -> loadAsModule (IRBuilder()) "ck" (junkRepo + "@tag:v1.0"))
     check "module: a checkout of an undecodable repo fails loudly, not with a stub message"
         (msg <> "<no exception>" && not (msg.Contains "pending")) msg)

    // Version stamp: mtime of the ONE mutable file, refspec-independent.
    check "stamp: versionStamp reads the repo file's mtime" (versionStamp junkRepo > 0L) $"{versionStamp junkRepo}"
    check "stamp: a refspec does not change the stamp (same repo file)"
        (versionStamp junkRepo = versionStamp (junkRepo + "@branch:main")) ""
    check "stamp: an unreadable repo stamps 0 rather than throwing"
        (versionStamp (Path.Combine(tmp, "nope.icechunk")) = 0L) ""

    check "fingerprint: 64 hex characters" ((fingerprint junkRepo).Length = 64) (fingerprint junkRepo)
    check "fingerprint: stable across calls" (fingerprint junkRepo = fingerprint junkRepo) ""
    check "fingerprint: distinguishes two checkouts of one repo"
        (fingerprint (junkRepo + "@branch:main") <> fingerprint (junkRepo + "@tag:v1.0")) ""
    check "fingerprint: never throws on a missing repo"
        ((fingerprint (Path.Combine(tmp, "nope.icechunk"))).Length = 64) ""

    // ---------------------------------------------------------------
    // 8. The ProviderSpec (registration surface + refusals)
    // ---------------------------------------------------------------
    printfn "\n--- provider spec ---"

    check "spec: registers under the surface module name 'icechunk'" (spec.Name = "icechunk") spec.Name
    check "spec: LinkNeeds stays 'none (pure std C++17)'"
        (spec.LinkNeeds = "none (pure std C++17)") spec.LinkNeeds
    check "spec: includes are std-only" (spec.Includes () |> List.contains "#include <fstream>") ""
    check "spec: load_compound is refused (GenReadCompoundVar = None)" spec.GenReadCompoundVar.IsNone ""
    check "spec: .stream is refused (GenStreamOpen/Fiber = None)"
        (spec.GenStreamOpen.IsNone && spec.GenStreamFiber.IsNone) ""
    check "spec: packed and wreath capabilities are declared (§8)"
        (spec.GenReadPacked.IsSome && spec.ReadWreathPool.IsSome) ""

    let arrType =
        let b = IRBuilder()
        { ElemType = IRTScalar ETFloat64
          IndexTypes = [ { Id = b.FreshId(); Rank = 1; Extent = IRLit (IRLitInt 4L); Symmetry = SymNone
                           Tag = None; IxKind = IxKPlain; Kind = SDimension; Dependencies = [] } ]
          IsVirtual = false; Identity = Some (AIDVariable "A") }

    (let msg = caught (fun () -> spec.GenWriteVar junkRepo "temp" "temp" arrType ["t"])
     check "spec: a write refuses loudly, citing writes-as-commits"
         (msg.Contains "COMMIT" && msg.Contains "conditional swap") msg)
    (let msg = caught (fun () -> spec.GenReadVar (Path.Combine(tmp, "nope.icechunk")) "temp" "temp" arrType)
     check "spec: a read of a missing repo names the REPO, not some inner seam"
         (msg.Contains "does not exist") msg)

    // STALE-PENDING-P1: these two used to pin "stops at the payload seam".
    // The live contract is that an UNDECODABLE repo still refuses loudly (and
    // real reads are section 10's business).
    (let msg = caught (fun () -> spec.GenReadVar junkRepo "temp" "temp" arrType)
     check "spec: a read of an undecodable repo refuses loudly, without a stub message"
         (msg <> "<no exception>" && not (msg.Contains "pending")) msg)
    (let r = spec.ReadVarData junkRepo "temp"
     check "spec: the compile-time fold refuses an undecodable repo loudly"
         (match r with Error e -> not (e.Contains "pending") | Ok _ -> false) (errorText r))

    (let r = spec.ReadVarData (Path.Combine(tmp, "nope.icechunk")) "temp"
     check "spec: the compile-time fold reports the missing repo"
         (isError r "does not exist") (errorText r))
    check "spec: VarDimNames is None-safe on an unreadable store"
        ((spec.VarDimNames (Path.Combine(tmp, "nope.icechunk")) "temp").IsNone) ""

    (try Directory.Delete(tmp, true) with _ -> ())

    // ---------------------------------------------------------------
    // 9. The fixture writer, on its own terms (no provider in the loop)
    // ---------------------------------------------------------------
    // Everything here is decidable from the DIRECTORY: which files exist,
    // what their names look like, what their first 39 bytes say, and how the
    // file set changes when the spec changes. The provider is deliberately
    // not consulted -- if both sides were wrong in the same way, section 10
    // would still pass and this section would not.
    printfn "\n--- fixture writer ---"

    // 5x4 chunked 3x2: a 2x2 chunk grid with edge overhang on BOTH axes
    // (rows 3-4 against a 3-row chunk, column 3 against a 2-column chunk).
    let tempV1 = Array.init 20 (fun i -> float (i + 1))            // 1 .. 20, sum 210
    let tempV2 = Array.init 20 (fun i -> float (i + 1) * 10.0)     // 10 .. 200, sum 2100
    let latData = [| 0.0; 1.0; 2.0; 3.0; 4.0 |]
    let lonData = [| 10.0; 11.0; 12.0; 13.0 |]

    /// The workhorse fixture: two commits over a fixed grid. `lat` and `lon`
    /// are byte-identical in both snapshots (small enough to ride INLINE),
    /// `temp` changes (and is forced NATIVE) -- which is precisely the
    /// "coordinate arrays untouched, data array rewritten" shape plan §5
    /// needs, and it falls out of content-derived ids rather than being
    /// staged by hand.
    let wxSpec (compress: bool) (snapshots: string list) : IW.RepoSpec =
        let coords = [
            IW.mkArray "lat" ["lat"] [5L] [5L] (IW.IceF64 latData)
            IW.mkArray "lon" ["lon"] [4L] [4L] (IW.IceF64 lonData) ]
        let tempOf (d: float[]) =
            { IW.mkArray "temp" ["lat"; "lon"] [5L; 4L] [3L; 2L] (IW.IceF64 d) with
                InlineThreshold = 0 }
        let snapOfName (n: string) =
            match n with
            | "s1" -> IW.mkSnapshot "s1" (coords @ [ tempOf tempV1 ])
            | "s2" -> IW.mkSnapshot "s2" (coords @ [ tempOf tempV2 ])
            | other -> failwithf "unknown fixture snapshot '%s'" other
        { IW.emptyRepo with
            Compress = compress
            Seed = 7
            Snapshots = snapshots |> List.map snapOfName
            Branches = (if List.contains "s2" snapshots then [ ("main", "s2") ] else [ ("main", "s1") ])
            Tags = [ ("v1.0", "s1") ]
            DeletedTags = [ "old" ] }

    let wxFull = wxSpec true ["s1"; "s2"]
    let wxRoot = fixRepo "ic_wx"
    let wxPlainRoot = fixRepo "ic_wx_plain"      // same repo, compression byte 0
    let wxOneRoot = fixRepo "ic_wx_one"          // s1 only: the id-reuse control

    let filesIn (dir: string) : string list =
        if Directory.Exists dir then
            Directory.GetFiles dir
            |> Array.map (fun (f: string) -> Path.GetFileName f)
            |> Array.sort
            |> List.ofArray
        else []

    (try
        IW.writeRepo wxRoot wxFull
        IW.writeRepo wxPlainRoot (wxSpec false ["s1"; "s2"])
        IW.writeRepo wxOneRoot (wxSpec true ["s1"])

        check "writer: the repo file exists" (File.Exists (Path.Combine(wxRoot, "repo"))) wxRoot
        check "writer: snapshots/ manifests/ chunks/ all exist"
            ([ "snapshots"; "manifests"; "chunks" ]
             |> List.forall (fun d -> Directory.Exists (Path.Combine(wxRoot, d)))) ""
        check "writer: transactions/ and overwritten/ exist but are empty (tx logs are PRUNABLE)"
            (Directory.Exists (Path.Combine(wxRoot, "transactions"))
             && List.isEmpty (filesIn (Path.Combine(wxRoot, "transactions")))) ""

        let snapFiles = filesIn (Path.Combine(wxRoot, "snapshots"))
        let manFiles = filesIn (Path.Combine(wxRoot, "manifests"))
        let chunkFiles = filesIn (Path.Combine(wxRoot, "chunks"))

        check "writer: two commits produce two snapshot files" (snapFiles.Length = 2) $"%A{snapFiles}"
        // lat and lon are identical in both commits, so their manifests are
        // written ONCE; temp differs, so it gets one manifest per commit.
        check "writer: manifests = lat + lon + temp@s1 + temp@s2 = 4 (coords shared)"
            (manFiles.Length = 4) $"%A{manFiles}"
        // lat (40 B) and lon (32 B) are under the 512-byte inline threshold and
        // have NO chunk files at all; temp's four 48-byte chunks are native, per commit.
        check "writer: chunks = 4 native temp chunks per commit = 8 (coords are inline)"
            (chunkFiles.Length = 8) $"%A{chunkFiles}"
        check "writer: every native chunk file is FULL SIZE (3*2 float64 = 48 B, edges padded)"
            (chunkFiles |> List.forall (fun f ->
                FileInfo(Path.Combine(wxRoot, "chunks", f)).Length = 48L))
            (chunkFiles |> List.map (fun f -> string (FileInfo(Path.Combine(wxRoot, "chunks", f)).Length))
                        |> String.concat ", ")

        check "writer: every object file name is 20 canonical Crockford characters"
            (snapFiles @ manFiles @ chunkFiles |> List.forall isObjectIdForm)
            (String.concat ", " (snapFiles @ manFiles @ chunkFiles))
        check "writer: the snapshot files are named by the ids `snapshotId` predicts"
            (List.sort [ IW.snapshotId wxFull "s1"; IW.snapshotId wxFull "s2" ] = snapFiles)
            $"%A{snapFiles}"

        // Header bytes, read straight off disk -- no provider parser involved.
        let head (f: string) = Array.sub (File.ReadAllBytes f) 0 39
        let repoHead = head (Path.Combine(wxRoot, "repo"))
        check "writer: the repo file's first 12 bytes are the magic"
            (Array.sub repoHead 0 12 = goldenMagic)
            (Array.sub repoHead 0 12 |> Array.map (sprintf "%02x") |> String.concat " ")
        check "writer: bytes 12..35 are the space-padded implementation name"
            (Text.Encoding.UTF8.GetString(repoHead, 12, 24)
              = "blade-fixtures" + String.replicate (24 - 14) " ")
            ("[" + Text.Encoding.UTF8.GetString(repoHead, 12, 24) + "]")
        check "writer: byte 36 is spec version 2" (repoHead.[36] = 2uy) $"{repoHead.[36]}"
        check "writer: byte 37 is file type 6 (RepoInfo)" (repoHead.[37] = 6uy) $"{repoHead.[37]}"
        check "writer: byte 38 is compression 1 (zstd) by default"
            (repoHead.[38] = 1uy) $"{repoHead.[38]}"
        check "writer: Compress = false stamps compression byte 0"
            ((head (Path.Combine(wxPlainRoot, "repo"))).[38] = 0uy) ""
        check "writer: snapshot files are stamped file type 1"
            (snapFiles |> List.forall (fun f -> (head (Path.Combine(wxRoot, "snapshots", f))).[37] = 1uy)) ""
        check "writer: manifest files are stamped file type 2"
            (manFiles |> List.forall (fun f -> (head (Path.Combine(wxRoot, "manifests", f))).[37] = 2uy)) ""
        check "writer: chunk files are RAW -- no 39-byte header, no magic"
            (chunkFiles |> List.forall (fun f ->
                let b = File.ReadAllBytes (Path.Combine(wxRoot, "chunks", f))
                b.Length = 48 && Array.sub b 0 3 <> [| 0x49uy; 0x43uy; 0x45uy |])) ""

        // Determinism: the same spec, written twice, is the same repo.
        let twinRoot = fixRepo "ic_wx_twin"
        IW.writeRepo twinRoot wxFull
        check "writer: the same spec written twice yields identical `repo` bytes"
            (File.ReadAllBytes (Path.Combine(twinRoot, "repo"))
              = File.ReadAllBytes (Path.Combine(wxRoot, "repo"))) ""
        check "writer: the same spec written twice yields the same object file names"
            (filesIn (Path.Combine(twinRoot, "chunks")) = chunkFiles
             && filesIn (Path.Combine(twinRoot, "manifests")) = manFiles) ""
        // Compression is a framing choice, not an identity one.
        check "writer: compression changes the file BYTES, not the object ids"
            (filesIn (Path.Combine(wxPlainRoot, "chunks")) = chunkFiles
             && filesIn (Path.Combine(wxPlainRoot, "manifests")) = manFiles
             && File.ReadAllBytes (Path.Combine(wxPlainRoot, "repo"))
                <> File.ReadAllBytes (Path.Combine(wxRoot, "repo"))) ""
        (try Directory.Delete(twinRoot, true) with _ -> ())

        // ID STABILITY -- the anchor of plan §5.2.
        check "writer: a node id depends on the array NAME, not on the snapshot"
            (IW.nodeId wxFull "temp" = IW.nodeId (wxSpec true ["s1"]) "temp"
             && IW.nodeId wxFull "lat" <> IW.nodeId wxFull "temp") (IW.nodeId wxFull "temp")
        check "writer: a node id is 13 Crockford characters"
            ((IW.nodeId wxFull "temp").Length = nodeIdChars
             && (IW.nodeId wxFull "temp") |> Seq.forall (fun c -> base32Alphabet.IndexOf c >= 0))
            (IW.nodeId wxFull "temp")
        check "writer: a different seed shares no ids"
            (IW.nodeId wxFull "temp" <> IW.nodeId { wxFull with Seed = 8 } "temp") ""

        // The one-snapshot control: every manifest and chunk file the s1-only
        // repo needs is REUSED verbatim by the two-snapshot repo. That is what
        // "the coordinate arrays did not change" means on disk.
        let oneMan = filesIn (Path.Combine(wxOneRoot, "manifests"))
        let oneChunk = filesIn (Path.Combine(wxOneRoot, "chunks"))
        check "writer: an s1-only repo has 3 manifests and 4 chunks"
            (oneMan.Length = 3 && oneChunk.Length = 4) $"%A{oneMan} / %A{oneChunk}"
        check "writer: adding a commit REUSES every untouched array's manifest and chunk files"
            (oneMan |> List.forall (fun f -> List.contains f manFiles)
             && oneChunk |> List.forall (fun f -> List.contains f chunkFiles))
            $"one=%A{oneMan @ oneChunk}"
        check "writer: the second commit adds exactly temp's new manifest and 4 new chunks"
            (manFiles.Length - oneMan.Length = 1 && chunkFiles.Length - oneChunk.Length = 4) ""
     with ex -> check "writer: fixture repos build" false ex.Message)

    // Placement policies, each in its own array of one repo, so a single
    // read loop in section 10 covers all four ChunkRef shapes.
    let polRoot = fixRepo "ic_policies"
    let polData (bias: float) = Array.init 20 (fun i -> bias + float (i + 1))
    let polSpec : IW.RepoSpec =
        let baseArr name d =
            IW.mkArray name ["r"; "c"] [5L; 4L] [3L; 2L] (IW.IceF64 d)
        { IW.emptyRepo with
            Seed = 11
            Snapshots = [
                IW.mkSnapshot "only" [
                    // Under the 512-byte default threshold: rides in the manifest.
                    baseArr "inl" (polData 0.0)
                    // Threshold 0: one chunk file per chunk, offset 0.
                    { baseArr "nat" (polData 100.0) with InlineThreshold = 0 }
                    // One file for the whole array: the refs carry NONZERO offsets.
                    { baseArr "pk" (polData 200.0) with InlineThreshold = 0; PackNativeChunks = true }
                    // Two manifests over disjoint ChunkIndexRanges on axis 0.
                    { baseArr "sp" (polData 300.0) with InlineThreshold = 0; SplitManifests = true }
                    // Chunk (1,1) has NO ref at all: it must read as fill_value.
                    { baseArr "fl" (polData 400.0) with
                        Fill = IW.IceFillFloat(-1.0)
                        OmitChunks = [ [1L; 1L] ] } ] ]
            Branches = [ ("main", "only") ] }

    (try
        IW.writeRepo polRoot polSpec
        let polChunks = filesIn (Path.Combine(polRoot, "chunks"))
        let polMans = filesIn (Path.Combine(polRoot, "manifests"))
        // nat: 4 files. pk: 1 file. sp: 4 files. inl and fl: inline, 0 files.
        check "writer: placement policies produce 4 + 1 + 4 = 9 chunk files"
            (polChunks.Length = 9) $"%A{polChunks}"
        check "writer: the packed array's single chunk file holds all four chunks (4*48 B)"
            (polChunks |> List.exists (fun f ->
                FileInfo(Path.Combine(polRoot, "chunks", f)).Length = 192L)) ""
        // inl(1) + nat(1) + pk(1) + sp(2) + fl(1) = 6
        check "writer: SplitManifests gives its array TWO manifests (6 in total)"
            (polMans.Length = 6) $"%A{polMans}"
     with ex -> check "writer: placement-policy fixture builds" false ex.Message)

    // Repos whose whole point is a refusal.
    let ambRoot = fixRepo "ic_ambiguous"
    let offlineRoot = fixRepo "ic_offline"
    let roRoot = fixRepo "ic_readonly"
    let spec1Root = fixRepo "ic_spec1"
    let virtRoot = fixRepo "ic_virtual"
    let packedRoot = fixRepo "ic_packed"

    /// A one-array, one-commit repo -- the base every refusal fixture varies.
    let tinySpec (seed: int) (arrays: IW.ArraySpec list) (branches: (string * string) list) : IW.RepoSpec =
        { IW.emptyRepo with
            Seed = seed
            Snapshots = [ IW.mkSnapshot "only" arrays ]
            Branches = branches }

    let tinyArr = IW.mkArray "a" ["r"] [4L] [2L] (IW.IceF64 [| 1.0; 2.0; 3.0; 4.0 |])

    (try
        // A branch AND a tag both named "x": the ambiguity the unit markers exist for.
        IW.writeRepo ambRoot
            { tinySpec 21 [ tinyArr ] [ ("x", "only") ] with Tags = [ ("x", "only") ] }
        IW.writeRepo offlineRoot
            { tinySpec 22 [ tinyArr ] [ ("main", "only") ] with
                Availability = generated.RepoAvailability.Offline
                AvailabilityReason = Some "fixture: marked unavailable" }
        IW.writeRepo roRoot
            { tinySpec 23 [ tinyArr ] [ ("main", "only") ] with
                Availability = generated.RepoAvailability.ReadOnly }
        IW.writeRepo spec1Root { tinySpec 24 [ tinyArr ] [ ("main", "only") ] with SpecByte = 1uy }
        IW.writeRepo virtRoot
            (tinySpec 25
                [ { IW.mkArray "v" ["r"] [4L] [2L] (IW.IceF64 [| 1.0; 2.0; 3.0; 4.0 |]) with
                      InlineThreshold = 0
                      VirtualChunk = Some ([ 1L ], "s3://elsewhere/bucket/chunk.bin") } ]
                [ ("main", "only") ])
        // A depth-1 SymIdx<2,4> pool: cardinality C(5,2) = 10, one flat axis.
        IW.writeRepo packedRoot
            (tinySpec 26
                [ { IW.mkArray "tri" [] [10L] [10L] (IW.IceF64 (Array.init 10 (fun i -> float i))) with
                      AttributesJson =
                        Some "\"blade\": {\"spec_version\": 1, \"layout\": \"packed\", \"order\": \"ascending-lex\", \"index_types\": [{\"kind\": \"sym\", \"rank\": 2, \"extent\": 4}], \"decomposition\": {\"scheme\": \"flat-ranges\"}}" } ]
                [ ("main", "only") ])
        check "writer: the refusal fixtures build" true ""
        check "writer: a SpecByte=1 fixture really carries spec byte 1"
            ((Array.sub (File.ReadAllBytes (Path.Combine(spec1Root, "repo"))) 0 39).[36] = 1uy) ""
        check "writer: the virtual-ref array writes NO chunk file for the virtual coordinate"
            (filesIn (Path.Combine(virtRoot, "chunks")) |> List.length = 1) ""
        // The writer rejects nonsense before it can produce a half-written repo.
        check "writer: a branch pointing at an undefined snapshot is refused"
            ((caught (fun () -> IW.writeRepo (fixRepo "ic_bad") (tinySpec 27 [ tinyArr ] [ ("main", "nope") ])))
                .Contains "which the spec does not define") ""
        check "writer: a data/shape mismatch is refused"
            ((caught (fun () ->
                IW.writeRepo (fixRepo "ic_bad")
                    (tinySpec 28 [ IW.mkArray "a" ["r"] [4L] [2L] (IW.IceF64 [| 1.0 |]) ] [ ("main", "only") ])))
                .Contains "cells but shape") ""
     with ex -> check "writer: refusal fixtures build" false ex.Message)

    // ---------------------------------------------------------------
    // 10. The provider, against REAL repos (the P1 contract)
    // ---------------------------------------------------------------
    // Everything below drives IcechunkProvider's PUBLIC surface over the
    // repos section 9 wrote. These are the behaviors the payload decoder has
    // to deliver -- they are written from the plan and the schemas, not from
    // any particular implementation of them.
    printfn "\n--- provider over real repos ---"

    let wxFullSpec = wxSpec true ["s1"; "s2"]
    let mainKey = wxRoot + "@branch:main"      // -> s2 (tempV2)
    let tagKey = wxRoot + "@tag:v1.0"          // -> s1 (tempV1)
    let bareKey = wxRoot + "@?:main"

    (try
        match load wxRoot with
        | Error e -> check "real: a bare path loads as a repo handle" false e
        | Ok (LoadedCheckout _) ->
            check "real: a bare path loads as a repo handle" false "got a checkout"
        | Ok (LoadedRepo handle) ->
            check "real: a bare path loads as a repo handle" true ""
            check "real: the repo file's branches come back"
                (handle.Info.Branches |> List.map fst = ["main"]) $"%A{handle.Info.Branches}"
            check "real: the repo file's tags come back"
                (handle.Info.Tags |> List.map fst = ["v1.0"]) $"%A{handle.Info.Tags}"
            check "real: deleted_tags tombstones come back"
                (handle.Info.DeletedTags = ["old"]) $"%A{handle.Info.DeletedTags}"
            check "real: both snapshots are listed" (handle.Info.Snapshots.Length = 2) $"{handle.Info.Snapshots.Length}"
            check "real: the status is Online" (handle.Info.Status = StatusOnline) (statusName handle.Info.Status)
            check "real: the header names the fixture writer"
                (handle.Header.Implementation = "blade-fixtures" && handle.Header.SpecVersion = 2)
                handle.Header.Implementation
            // Ref resolution against the REAL repo file, cross-checked
            // against the id the writer says it minted.
            check "real: branch 'main' resolves to snapshot s2"
                (resolveRef handle.Info RefBranch "main" = Ok (IW.snapshotIdBytes wxFullSpec "s2"))
                (errorText (resolveRef handle.Info RefBranch "main"))
            check "real: tag 'v1.0' resolves to snapshot s1"
                (resolveRef handle.Info RefTag "v1.0" = Ok (IW.snapshotIdBytes wxFullSpec "s1"))
                (errorText (resolveRef handle.Info RefTag "v1.0"))
            check "real: the bare form resolves 'main' uniquely"
                (resolveRef handle.Info RefBare "main" = Ok (IW.snapshotIdBytes wxFullSpec "s2")) ""
            check "real: a snapshot id resolves under ic.snapshot"
                (resolveRef handle.Info RefSnapshot (IW.snapshotId wxFullSpec "s1")
                  = Ok (IW.snapshotIdBytes wxFullSpec "s1"))
                (errorText (resolveRef handle.Info RefSnapshot (IW.snapshotId wxFullSpec "s1")))
            check "real: a deleted tag names its tombstone"
                (isError (resolveRef handle.Info RefBare "old") "DELETED TAG")
                (errorText (resolveRef handle.Info RefBare "old"))
     with ex -> check "real: bare repo-handle load" false ex.Message)

    (try
        match load mainKey with
        | Error e -> check "real: a branch key loads as a checkout" false e
        | Ok (LoadedRepo _) -> check "real: a branch key loads as a checkout" false "got a repo handle"
        | Ok (LoadedCheckout ck) ->
            check "real: a branch key loads as a checkout" true ""
            check "real: the checkout carries the resolved snapshot id"
                (ck.SnapshotId = IW.snapshotIdBytes wxFullSpec "s2") (base32Encode ck.SnapshotId)
            check "real: the checkout's root-level arrays are lat, lon, temp"
                (List.sort (arrayNames ck) = ["lat"; "lon"; "temp"]) $"%A{arrayNames ck}"
            // The snapshot really does carry a root GROUP node -- the decoder
            // has to read the NodeData union's tag, not assume everything is
            // an array -- and that node is NOT offered as a variable.
            check "real: the root '/' node decodes as a GROUP and is not a variable"
                ((ck.Snapshot.Nodes |> List.exists (fun n -> n.Path = "/" && n.Kind = NodeGroup))
                 && (arrayNames ck).Length = 3)
                (sprintf "%A" (ck.Snapshot.Nodes |> List.map (fun n -> (n.Path, n.Kind))))
            (match findArray ck "temp" with
             | Error e -> check "real: temp's zarr.json parses with the Zarr v3 parser" false e
             | Ok (node, meta) ->
                 check "real: temp's zarr.json parses with the Zarr v3 parser" true ""
                 check "real: shape and chunks survive the round trip"
                     (meta.Shape = [5L; 4L] && meta.Chunks = [3L; 2L]) $"%A{meta.Shape} / %A{meta.Chunks}"
                 check "real: dimension names survive the round trip"
                     (meta.DimNames = Some ["lat"; "lon"]) $"%A{meta.DimNames}"
                 check "real: the element type is float64" (meta.Dtype.Elem = ETFloat64) meta.Dtype.Code
                 check "real: the node id is the one the writer minted"
                     (node.Id = IW.nodeIdBytes wxFullSpec "temp") (base32Encode node.Id)
                 check "real: the structural dimension names agree with the JSON"
                     (node.DimensionNames = Some ["lat"; "lon"]) $"%A{node.DimensionNames}"
                 check "real: the array points at exactly one manifest"
                     (node.ManifestRefs.Length = 1) $"{node.ManifestRefs.Length}")
            // Node ids are STABLE across snapshots, and the CHUNK-REF TABLE is
            // what decides whether two checkouts present the same axis
            // (plan §5.2): unchanged coordinate arrays keep their node id,
            // their user_data bytes AND their manifest ids, while the array
            // that was rewritten points somewhere else. All three are
            // decidable from metadata alone -- no chunk is read here.
            (match load tagKey with
             | Ok (LoadedCheckout ck1) ->
                 let nodeOf (c: CheckoutHandle) (n: string) =
                     match findArray c n with
                     | Ok (node, _) -> Some node
                     | Error _ -> None
                 let idOf c n = nodeOf c n |> Option.map (fun x -> base32Encode x.Id)
                 let jsonOf c n = nodeOf c n |> Option.map (fun x -> x.UserDataJson)
                 let manOf c n =
                     nodeOf c n |> Option.map (fun x -> x.ManifestRefs |> List.map (fst >> base32Encode))
                 check "real: an array keeps its node id across snapshots"
                     (idOf ck1 "temp" = idOf ck "temp" && idOf ck1 "lat" = idOf ck "lat")
                     (sprintf "%A vs %A" (idOf ck1 "temp") (idOf ck "temp"))
                 check "real: two different arrays have different node ids"
                     (idOf ck "lat" <> idOf ck "temp" && (idOf ck "lat").IsSome) ""
                 check "real: an untouched coordinate array keeps its user_data bytewise"
                     (jsonOf ck1 "lat" = jsonOf ck "lat" && (jsonOf ck "lat").IsSome) ""
                 check "real: an untouched coordinate array keeps its manifest ids"
                     (manOf ck1 "lat" = manOf ck "lat" && (manOf ck "lat").IsSome)
                     (sprintf "%A vs %A" (manOf ck1 "lat") (manOf ck "lat"))
                 check "real: the REWRITTEN array's manifest id DIFFERS between snapshots"
                     (manOf ck1 "temp" <> manOf ck "temp" && (manOf ck "temp").IsSome)
                     (sprintf "%A vs %A" (manOf ck1 "temp") (manOf ck "temp"))
             | Ok (LoadedRepo _) -> check "real: the tag key loads as a checkout" false "got a repo handle"
             | Error e -> check "real: the tag key loads as a checkout" false e)
            // A name that is not there names what IS there.
            (let r = findArray ck "nope"
             check "real: a missing variable lists the checkout's root-level arrays"
                 (isError r "not found" && isError r "temp") (errorText r))
     with ex -> check "real: branch checkout" false ex.Message)

    // The checkout module: dims and vars structs, built through the Zarr
    // provider's own module builder over verbatim zarr.json.
    (try
        let m = loadAsModule (IRBuilder()) "ck" mainKey
        let indexDefs = m.Types |> List.choose (function IRTDIndexType (n, it) -> Some (n, it) | _ -> None)
        let dimsFields =
            m.Types |> List.tryPick (function IRTDStruct ("ck__dims", fs) -> Some fs | _ -> None)
            |> Option.defaultValue []
        let varsFields =
            m.Types |> List.tryPick (function IRTDStruct ("ck__vars", fs) -> Some fs | _ -> None)
            |> Option.defaultValue []
        check "module: a checkout key builds named index types lat and lon"
            (indexDefs |> List.map fst |> List.sort = ["lat"; "lon"]) $"%A{List.map fst indexDefs}"
        check "module: dims carries lat and lon"
            (dimsFields |> List.map fst |> List.sort = ["lat"; "lon"]) $"%A{List.map fst dimsFields}"
        check "module: vars carries temp only (lat/lon are coordinate arrays)"
            (varsFields |> List.map fst = ["temp"]) $"%A{List.map fst varsFields}"
        let tempIds =
            varsFields |> List.tryPick (fun (n, t) ->
                if n <> "temp" then None else
                match t with
                | ArrayElem at -> Some (at.IndexTypes |> List.map (_.Id))
                | _ -> None)
        let latId = indexDefs |> List.tryPick (fun (n, it) -> if n = "lat" then Some it.Id else None)
        let sharesLat =
            match tempIds, latId with
            | Some (t0 :: _), Some l -> t0 = l
            | _ -> false
        check "module: temp's first index IS the shared lat index type (same Id)"
            sharesLat (sprintf "temp ids %A, lat id %A" tempIds latId)
     with ex -> check "module: checkout builds a dims/vars module" false ex.Message)

    (try
        let m = loadAsModule (IRBuilder()) "wx" wxRoot
        check "module: a bare path binds an EMPTY repo-handle module (no dims, no vars)"
            (m.Name = "wx" && m.Types.IsEmpty && m.Bindings.IsEmpty) $"%A{m.Types}"
     with ex -> check "module: a bare path binds an empty repo-handle module" false ex.Message)

    // A branch and the snapshot id it points at name the SAME snapshot, so
    // they must build the same module shape.
    (try
        let snapKey = wxRoot + "@snapshot:" + IW.snapshotId wxFullSpec "s2"
        let byId = loadAsModule (IRBuilder()) "ck" snapKey
        let byBranch = loadAsModule (IRBuilder()) "ck" mainKey
        check "module: a snapshot-id key builds the same module as the branch pointing at it"
            (List.length byId.Types = List.length byBranch.Types
             && List.length byId.Types > 0)
            (sprintf "%d vs %d types" (List.length byId.Types) (List.length byBranch.Types))
     with ex -> check "module: a snapshot-id key builds the same module as its branch" false ex.Message)

    // Dense reads: the whole point. Both compression modes, all four
    // ChunkRef shapes, and an edge-chunk grid throughout.
    printfn "\n--- provider dense reads ---"

    let readsAs (key: string) (varName: string) (expected: float[]) (dims: int list) (label: string) =
        match spec.ReadVarData key varName with
        | Error e -> check ($"read: {label}") false e
        | Ok data ->
            check ($"read: {label} -- dim lengths") (data.DimLengths = dims) (sprintf "%A" data.DimLengths)
            match data.Payload with
            | Blade.ProviderRegistry.PFloats got ->
                check ($"read: {label} -- values")
                    (got.Length = expected.Length
                     && Array.forall2 (fun (a: float) (b: float) -> abs (a - b) <= 1e-12 * max 1.0 (abs b)) got expected)
                    (if got.Length <> expected.Length then $"{got.Length} vs {expected.Length} values"
                     else sprintf "%A" (Array.truncate 8 got))
            | Blade.ProviderRegistry.PInts _ ->
                check ($"read: {label} -- values") false "payload came back as int64, not float"

    (try
        readsAs mainKey "temp" tempV2 [5; 4] "branch main -> s2 (native chunks, zstd)"
        readsAs tagKey "temp" tempV1 [5; 4] "tag v1.0 -> s1 (native chunks, zstd)"
        readsAs bareKey "temp" tempV2 [5; 4] "bare '?' form resolves to the branch"
        readsAs (wxRoot + "@snapshot:" + IW.snapshotId wxFullSpec "s1") "temp" tempV1 [5; 4]
                "snapshot id -> s1"
        readsAs mainKey "lat" latData [5] "coordinate array (INLINE chunk)"
        readsAs mainKey "lon" lonData [4] "second coordinate array (INLINE chunk)"
        readsAs (wxPlainRoot + "@branch:main") "temp" tempV2 [5; 4]
                "the same repo with compression byte 0"
     with ex -> check "read: dense reads over the workhorse fixture" false ex.Message)

    (try
        readsAs (polRoot + "@?:main") "inl" (polData 0.0) [5; 4] "policy inl -- every chunk INLINE"
        readsAs (polRoot + "@?:main") "nat" (polData 100.0) [5; 4] "policy nat -- one file per chunk"
        readsAs (polRoot + "@?:main") "pk" (polData 200.0) [5; 4]
                "policy pk -- packed file, NONZERO ChunkRef offsets"
        readsAs (polRoot + "@?:main") "sp" (polData 300.0) [5; 4]
                "policy sp -- two manifests over disjoint ChunkIndexRanges"
        // Chunk (1,1) of a 5x4/3x2 grid covers rows [3,5) x cols [2,4):
        // flat indices 14, 15, 18, 19. With no ref, those read as fill (-1).
        let flExpect =
            let a = Array.copy (polData 400.0)
            for i in [14; 15; 18; 19] do a.[i] <- -1.0
            a
        readsAs (polRoot + "@?:main") "fl" flExpect [5; 4]
                "policy fl -- an ABSENT chunk reads as fill_value"
     with ex -> check "read: placement-policy reads" false ex.Message)

    // Named refusals, over repos that really are what they claim.
    printfn "\n--- provider refusals over real repos ---"

    (try
        (let r = load (ambRoot + "@?:x")
         check "refuse: a bare ref naming BOTH a branch and a tag is ambiguous"
             (isError r "ambiguous" && isError r "ic.branch" && isError r "ic.tag") (errorText r))
        check "refuse: the markers disambiguate the colliding name"
            (match load (ambRoot + "@branch:x"), load (ambRoot + "@tag:x") with
             | Ok _, Ok _ -> true
             | _ -> false)
            (errorText (load (ambRoot + "@branch:x")) + " | " + errorText (load (ambRoot + "@tag:x")))

        (let r = load (offlineRoot + "@branch:main")
         check "refuse: an Offline repo refuses a checkout by name" (isError r "Offline") (errorText r))
        // Plan §3: the repo file is parsed AT LOAD, so an Offline repo fails
        // at the load site rather than at the first checkout.
        (let msg = caught (fun () -> loadAsModule (IRBuilder()) "wx" offlineRoot)
         check "refuse: an Offline repo fails at the bare LOAD site, not at first checkout"
             (msg.Contains "Offline") msg)
        check "refuse: a ReadOnly repo reads normally (only Offline refuses)"
            (match load (roRoot + "@branch:main") with Ok _ -> true | Error _ -> false)
            (errorText (load (roRoot + "@branch:main")))

        (let r = load spec1Root
         check "refuse: a real spec-1 repo refuses by name at the load site"
             (isError r "spec version 1") (errorText r))

        (let r = spec.ReadVarData (virtRoot + "@?:main") "v"
         check "refuse: a VIRTUAL chunk ref is refused by name"
             (isError r "VIRTUAL chunk refs are not supported") (errorText r))
        (let msg = caught (fun () -> spec.GenReadVar (virtRoot + "@?:main") "v" "v" arrType)
         check "refuse: the emitted reader refuses a virtual ref too"
             (msg.Contains "VIRTUAL chunk refs are not supported") msg)

        // Packed pools do not FOLD (StaticValue has no packed carrier), so the
        // fold path steers to `ic.read` -- the same steering the Zarr provider
        // applies, word for word.
        (let r = spec.ReadVarData (packedRoot + "@?:main") "tri"
         check "refuse: a packed pool steers the compile-time fold to ic.read"
             (isError r "packed (blade: layout=packed) pool layout"
              && isError r "do not fold at compile time") (errorText r))
        (let r = spec.ReadVarData (wxRoot + "@branch:main") "nope"
         check "refuse: a missing variable in a real checkout lists what IS there"
             (isError r "not found" && isError r "temp") (errorText r))
        (let r = spec.ReadVarData wxRoot "temp"
         check "refuse: reading through a bare REPO HANDLE says to check a ref out first"
             (isError r "REPO HANDLE" && isError r "checkout") (errorText r))
        // Wreath capability: present, and it refuses anything that is not an
        // orbit head rather than mistyping the pool.
        (match spec.ReadWreathPool with
         | Some readPool ->
             let r = readPool (packedRoot + "@?:main") "tri"
             check "refuse: a depth-1 packed array is not an orbit pool"
                 (isError r "depth-1 packed" || isError r "not an orbit") (errorText r)
             let r2 = readPool (wxRoot + "@branch:main") "temp"
             check "refuse: a dense array is not an orbit pool"
                 (isError r2 "not an orbit") (errorText r2)
         | None -> check "refuse: the wreath capability is declared" false "ReadWreathPool = None")
     with ex -> check "refuse: named refusals over real repos" false ex.Message)

    // VarDimNames, VersionStamp and Fingerprint over a real repo.
    (try
        let tempDims = spec.VarDimNames mainKey "temp"
        check "provenance: VarDimNames reads the snapshot's structural names"
            (tempDims = Some ["lat"; "lon"]) (sprintf "%A" tempDims)
        check "provenance: VarDimNames is None-safe on a repo handle"
            ((spec.VarDimNames wxRoot "temp").IsNone) ""
        check "provenance: the version stamp is the ONE mutable file's mtime"
            (spec.VersionStamp wxRoot > 0L && spec.VersionStamp wxRoot = spec.VersionStamp mainKey) ""
        check "provenance: the fingerprint separates two checkouts of one repo"
            (spec.Fingerprint mainKey <> spec.Fingerprint tagKey
             && (spec.Fingerprint mainKey).Length > 0) ""
     with ex -> check "provenance over a real repo" false ex.Message)

    // ---------------------------------------------------------------
    // 11. Runtime read e2e (g++; fixture repos generated on the fly)
    // ---------------------------------------------------------------
    // The full arc: `ic.load` -> `repo.checkout` -> `ic.read` -> a reduce,
    // compiled to C++ and run. Two programs, because the two checkout forms
    // take different paths through the desugar: the BARE form (resolved
    // across namespaces) and the MARKER form (`ic.tag`).
    printfn "\n--- read e2e: ic.load / repo.checkout / ic.read ---"

    let e2eDir = "./generated_cpp_tests"
    if not (Directory.Exists e2eDir) then Directory.CreateDirectory e2eDir |> ignore

    /// `name = <number>` on its own line, InvariantCulture.
    let printedScalar (name: string) (out: string) : float option =
        out.Split('\n')
        |> Array.tryPick (fun l ->
            let l = l.Trim()
            let prefix = name + " = "
            if l.StartsWith prefix then
                match Double.TryParse(l.Substring prefix.Length,
                                      Globalization.NumberStyles.Float,
                                      Globalization.CultureInfo.InvariantCulture) with
                | true, v -> Some v
                | _ -> None
            else None)

    (try
        // The same relative path has to resolve at BOTH working directories:
        // the compiler's (compile-time ref resolution + chunk-table baking)
        // and the executable's (runtime chunk reads). Same split as
        // ZarrTests' store mirroring.
        let e2eRepo = fixRepo "ic_e2e"
        let e2eSpec = wxSpec true ["s1"; "s2"]
        IW.writeRepoAt [ e2eRepo; Path.Combine(e2eDir, e2eRepo) ] e2eSpec

        // (label, source, expected `total`, expected checkout)
        //   main -> s2 -> tempV2, sum 2100      v1.0 -> s1 -> tempV1, sum 210
        let programs =
            [ ("bare", "repo.checkout(\"main\")", 2100.0)
              ("marker", "repo.checkout(\"v1.0\", ic.tag)", 210.0) ]

        for (label, checkoutExpr, expectedTotal) in programs do
            let src =
                sprintf """
import icechunk as ic

let repo = ic.load("%s")
let ck = %s
let A = ck.vars.temp |> ic.read
let total = reduce(A, (+), axes = 2)
"""
                        e2eRepo checkoutExpr
            match lower src with
            | Error e -> check ($"e2e {label}: lowers") false e
            | Ok ir ->
                check ($"e2e {label}: ProviderReads spec (provider=icechunk, var=temp)")
                    (ir.Modules.[0].ProviderReads
                     |> Map.exists (fun _ s -> s.Provider = "icechunk" && s.VarName = "temp"))
                    ""
                let (cppCode, _) = CodeGen.genSelfContainedProgramFromIR ir ($"icechunk_read_e2e_{label}")
                // The whole point of resolving at compile time: no Icechunk
                // logic reaches the binary -- no FlatBuffers, no zstd, just
                // std::ifstream over baked offsets.
                check ($"e2e {label}: emits fstream reads and NO icechunk/zstd/flatbuffers dependency")
                    (cppCode.Contains "std::ifstream"
                     && not (cppCode.Contains "zstd") && not (cppCode.Contains "flatbuffers")
                     && not (cppCode.Contains "netcdf.h")) ""
                CodeGen.deployRuntimeHeaders e2eDir
                let cppFile = Path.Combine(e2eDir, $"icechunk_read_e2e_{label}.cpp")
                File.WriteAllText(cppFile, cppCode)
                (match compileCpp cppFile e2eDir with
                 | Ok exePath ->
                     check ($"e2e {label}: compiles (pure std C++ -- no link flags)") true ""
                     (match runExecutable exePath with
                      | Ok (0, runOut) ->
                          check ($"e2e {label}: runs (exit 0)") true ""
                          check ($"e2e {label}: total = sum over the checked-out snapshot = {expectedTotal}")
                              (match printedScalar "total" runOut with
                               | Some v -> abs (v - expectedTotal) <= 1e-9
                               | None -> false)
                              runOut
                      | Ok (code, runOut) ->
                          check ($"e2e {label}: runs (exit 0)") false ($"exit {code}: {runOut}")
                      | Error e -> check ($"e2e {label}: runs (exit 0)") false e)
                 | Error e -> baselineFailed ($"icechunk read e2e {label}") e)

        // A repo that is gone at RUN time must die loudly, not read zeros:
        // the compiled binary holds baked paths into a directory that a GC
        // or an expiration can remove out from under it.
        (let missingDir = Path.Combine(Path.GetTempPath(), "blade_ic_missing_" + Guid.NewGuid().ToString("N"))
         Directory.CreateDirectory missingDir |> ignore
         try
            let exeName = "icechunk_read_e2e_bare" + (if OperatingSystem.IsWindows() then ".exe" else "")
            let builtExe = Path.Combine(e2eDir, exeName)
            if File.Exists builtExe then
                let exeCopy = Path.Combine(missingDir, exeName)
                File.Copy(builtExe, exeCopy, true)
                match runExecutable exeCopy with
                | Ok (code, missOut) ->
                    let headOfOut = missOut.Substring(0, min 200 missOut.Length)
                    check "e2e: a repo missing at run time fails loudly (nonzero exit)"
                        (code <> 0) ($"exit {code}: " + headOfOut)
                | Error e -> check "e2e: a repo missing at run time fails loudly" false e
         finally
            try Directory.Delete(missingDir, true) with _ -> ())
     with ex -> check "e2e: icechunk read" false ex.Message)

    // ---------------------------------------------------------------
    // 12. Axis identity across checkouts (plan §5)
    // ---------------------------------------------------------------
    // Two checkouts of one repo share the index type for a dimension IFF the
    // AXIS is unchanged between their snapshots: same name, same extent, and
    // -- when a coordinate variable named after the dim exists -- the same
    // coordinate content, decided from METADATA ALONE (node id, user_data
    // bytes, chunk-ref table). No chunk is read to answer the question.
    //
    // The observable consequence is the point, and it is asserted at BOTH
    // levels: the index types two modules carry have the same `Id` (which is
    // what `unify` compares), and a source-level program that subtracts one
    // checkout's variable from another's typechecks -- or, when the axis
    // diverged, refuses.
    printfn "\n--- axis identity across checkouts ---"

    /// `dim -> index-type Id` for a checkout module. Ids are what unification
    /// compares, so two checkouts SHARE an axis exactly when these agree.
    let axisIdsOf (m: IRModule) : Map<string, int> =
        m.Types
        |> List.choose (function IRTDIndexType (n, it) -> Some (n, it.Id) | _ -> None)
        |> Map.ofList

    /// The index-type Ids of one variable of a checkout module, in order --
    /// the array type's OWN slots, not the module's type defs.
    let varIdsOf (m: IRModule) (binding: string) (varName: string) : int list option =
        m.Types
        |> List.tryPick (function IRTDStruct (n, fs) when n = binding + "__vars" -> Some fs | _ -> None)
        |> Option.defaultValue []
        |> List.tryPick (fun (n, t) ->
            if n <> varName then None else
            match t with
            | ArrayElem at -> Some (at.IndexTypes |> List.map (_.Id))
            | _ -> None)

    let axisModule (binding: string) (key: string) : IRModule =
        loadAsModule (IRBuilder()) binding key

    /// The refusal a diverged axis earns: the ordinary NAMED-AXIS refusal, in
    /// whichever of its two wordings applies -- the rank->=2 product rule
    /// ("same axis tags and extents") for a 2-D variable, BL3999's rank-1
    /// co-iteration message for a 1-D one. Both are the same fact: two index
    /// types with different names are not one index space.
    let refusesOnAxes (r: Result<'a, string>) : bool =
        match r with
        | Error e -> e.Contains "same axis tags and extents" || e.Contains "DIFFERENT index types"
        | Ok _ -> false

    /// A cross-checkout differencing program over one repo: the §5 headline.
    /// `ck2.vars.temp - ck1.vars.temp` typechecks only if the two checkouts'
    /// axes unify.
    let crossCheckoutSrc (root: string) =
        sprintf """
import icechunk as ic

let repo = ic.load("%s")
let ck1 = repo.checkout("v1.0", ic.tag)
let ck2 = repo.checkout("main")
let A = ck1.vars.temp |> ic.read
let B = ck2.vars.temp |> ic.read
let d = B - A
let total = reduce(d, (+), axes = 2)
"""
                root

    // Every axis fixture is its own repo root, so one section's identities can
    // never answer another's -- `repoPath` is half the mint-table key.
    let axisCoordRoot = fixRepo "ic_axis_coord"      // coordinate REWRITTEN, same extent
    let axisRegridRoot = fixRepo "ic_axis_regrid"    // coordinate REGRIDDED, new extent
    let axisNoCoordRoot = fixRepo "ic_axis_nocoord"  // dims with NO coordinate variable
    let axisTwinRoot = fixRepo "ic_axis_twin"        // a byte-identical SECOND repo

    let latShifted = [| 100.0; 101.0; 102.0; 103.0; 104.0 |]
    let lat6 = [| 0.0; 1.0; 2.0; 3.0; 4.0; 5.0 |]
    let temp6x4 = Array.init 24 (fun i -> float (i + 1))

    /// Same grid, same `temp`, `lat` rewritten between the two commits. The
    /// coordinate array's node id is unchanged (ids are derived from the NAME),
    /// so what has to notice the difference is the chunk-ref table.
    let coordRewriteSpec : IW.RepoSpec =
        let lon = IW.mkArray "lon" ["lon"] [4L] [4L] (IW.IceF64 lonData)
        let temp = { IW.mkArray "temp" ["lat"; "lon"] [5L; 4L] [3L; 2L] (IW.IceF64 tempV1) with
                       InlineThreshold = 0 }
        let lat (d: float[]) = IW.mkArray "lat" ["lat"] [5L] [5L] (IW.IceF64 d)
        { IW.emptyRepo with
            Seed = 11
            Snapshots = [ IW.mkSnapshot "s1" [ lat latData; lon; temp ]
                          IW.mkSnapshot "s2" [ lat latShifted; lon; temp ] ]
            Branches = [ ("main", "s2") ]
            Tags = [ ("v1.0", "s1") ] }

    /// A regrid: `lat` (and therefore `temp`) changes EXTENT between commits.
    let regridSpec : IW.RepoSpec =
        let lon = IW.mkArray "lon" ["lon"] [4L] [4L] (IW.IceF64 lonData)
        { IW.emptyRepo with
            Seed = 12
            Snapshots =
                [ IW.mkSnapshot "s1"
                    [ IW.mkArray "lat" ["lat"] [5L] [5L] (IW.IceF64 latData); lon
                      { IW.mkArray "temp" ["lat"; "lon"] [5L; 4L] [3L; 2L] (IW.IceF64 tempV1) with
                          InlineThreshold = 0 } ]
                  IW.mkSnapshot "s2"
                    [ IW.mkArray "lat" ["lat"] [6L] [6L] (IW.IceF64 lat6); lon
                      { IW.mkArray "temp" ["lat"; "lon"] [6L; 4L] [3L; 2L] (IW.IceF64 temp6x4) with
                          InlineThreshold = 0 } ] ]
            Branches = [ ("main", "s2") ]
            Tags = [ ("v1.0", "s1") ] }

    /// `temp` over named dimensions with NO coordinate arrays: §5.2 condition
    /// (3) is vacuous, so name + extent IS the identity.
    let noCoordSpec : IW.RepoSpec =
        let temp (d: float[]) =
            { IW.mkArray "temp" ["row"; "col"] [5L; 4L] [3L; 2L] (IW.IceF64 d) with
                InlineThreshold = 0 }
        { IW.emptyRepo with
            Seed = 13
            Snapshots = [ IW.mkSnapshot "s1" [ temp tempV1 ]; IW.mkSnapshot "s2" [ temp tempV2 ] ]
            Branches = [ ("main", "s2") ]
            Tags = [ ("v1.0", "s1") ] }

    (try
        IW.writeRepo axisCoordRoot coordRewriteSpec
        IW.writeRepo axisRegridRoot regridSpec
        IW.writeRepo axisNoCoordRoot noCoordSpec
        IW.writeRepo axisTwinRoot wxFull
        // The fixtures were just (re)written at paths earlier sections may
        // already have read; drop the mtime-keyed memos AND the identities
        // decided from them before asking anything.
        resetCaches ()

        // (a) The same ref, checked out twice. Nothing changed because nothing
        // could have: every axis compares equal to itself.
        let sameA = axisModule "ck1" mainKey
        let sameB = axisModule "ck2" mainKey
        check "axis: the same ref checked out twice shares every axis identity"
            (axisIdsOf sameA = axisIdsOf sameB && (axisIdsOf sameA).Count = 2)
            (sprintf "%A vs %A" (axisIdsOf sameA) (axisIdsOf sameB))
        check "axis: two checkouts of one ref give `temp` the same index slots"
            (varIdsOf sameA "ck1" "temp" = varIdsOf sameB "ck2" "temp"
             && (varIdsOf sameA "ck1" "temp").IsSome)
            (sprintf "%A vs %A" (varIdsOf sameA "ck1" "temp") (varIdsOf sameB "ck2" "temp"))

        // (b) THE HEADLINE. `main` and `v1.0` differ only in `temp`'s cells --
        // the coordinate arrays are untouched, so their node ids, user_data and
        // manifest ids are identical and the axes are the SAME axes.
        let mainM = axisModule "ck2" mainKey
        let tagM = axisModule "ck1" tagKey
        check "axis: a data-only commit leaves lat and lon SHARED across checkouts"
            (axisIdsOf mainM = axisIdsOf tagM && (axisIdsOf mainM).Count = 2)
            (sprintf "%A vs %A" (axisIdsOf mainM) (axisIdsOf tagM))
        check "axis: the differencing pair's `temp` arrays carry the same index slots"
            (varIdsOf mainM "ck2" "temp" = varIdsOf tagM "ck1" "temp"
             && (varIdsOf mainM "ck2" "temp") = Some [ (axisIdsOf mainM).["lat"]; (axisIdsOf mainM).["lon"] ])
            (sprintf "%A vs %A" (varIdsOf mainM "ck2" "temp") (varIdsOf tagM "ck1" "temp"))
        check "axis: a shared axis records ONE identity, presented by both refs"
            (match axisIdentities wxRoot "lat" with
             | [ one ] -> List.sort one.Refs = ["branch:main"; "tag:v1.0"] && one.CoordFP.IsSome
             | _ -> false)
            (sprintf "%A" (axisIdentities wxRoot "lat" |> List.map (fun i -> (i.Refs, i.SplitReason))))
        check "axis: the shared identity is a NAMED axis, and both checkouts use that name"
            (match axisIdentities wxRoot "lat" with
             | [ one ] ->
                 (defaultArg one.IndexType.Tag "").StartsWith (axisTagPrefix + "lat@")
                 && one.IndexType.Id >= axisIdBase
             | _ -> false)
            (sprintf "%A" (axisIdentities wxRoot "lat" |> List.map (fun i -> (i.IndexType.Tag, i.IndexType.Id))))

        // ... and the consequence that matters: the program compiles.
        (match lower (crossCheckoutSrc wxRoot) with
         | Ok _ -> check "axis: `ck2.vars.temp - ck1.vars.temp` TYPECHECKS across a data-only commit" true ""
         | Error e ->
             check "axis: `ck2.vars.temp - ck1.vars.temp` TYPECHECKS across a data-only commit" false e)

        // Naming an axis must not make ORDINARY use of a checkout noisy. The
        // tag is `__`-prefixed exactly so the three seams that read a tag as a
        // user-facing NAME stay quiet: no BL4003 "indexed with untagged
        // integer" on a plain subscript, and an alias through
        // `<binding>.index.<dim>` (which re-tags with the ALIAS name) still
        // ascribes. Both regressed when the tag was a plain name.
        (let quiet =
            sprintf """
import icechunk as ic

let repo = ic.load("%s")
let ck = repo.checkout("main")
let A = ck.vars.temp |> ic.read
type Lat = ck.index.lat
type Lon = ck.index.lon
let cell = A(2, 1)
let B: Array<Float64 like Lat, Lon> = A
let total = reduce(B, (+), axes = 2)
"""
                    wxRoot
         let (r, warns) = Blade.Lowering.lowerCaptured quiet
         check "axis: a named axis does not make ordinary subscripting noisy (no BL4003)"
             ((match r with Ok _ -> true | Error _ -> false)
              && not (warns |> List.exists (fun (d: Blade.Diagnostics.Diagnostic) -> d.Code = "BL4003")))
             (match r with
              | Error e -> e
              | Ok _ -> warns |> List.map (fun d -> d.Code + ": " + d.Message) |> String.concat "; "))

        // (c) The coordinate variable itself was rewritten (same extent, same
        // node id): a different axis, and arithmetic across it refuses.
        let coordMain = axisModule "ck2" (axisCoordRoot + "@branch:main")
        let coordTag = axisModule "ck1" (axisCoordRoot + "@tag:v1.0")
        check "axis: a REWRITTEN coordinate splits the identity (lat differs)"
            ((axisIdsOf coordMain).["lat"] <> (axisIdsOf coordTag).["lat"])
            (sprintf "%A vs %A" (axisIdsOf coordMain) (axisIdsOf coordTag))
        check "axis: the split is PER-AXIS -- untouched lon still shares"
            ((axisIdsOf coordMain).["lon"] = (axisIdsOf coordTag).["lon"])
            (sprintf "%A vs %A" (axisIdsOf coordMain) (axisIdsOf coordTag))
        check "axis: the split records WHY (coordinate content, not extent)"
            (match axisIdentities axisCoordRoot "lat" with
             | newest :: _ :: [] -> newest.SplitReason = Some "coordinate content differs"
             | _ -> false)
            (sprintf "%A" (axisIdentities axisCoordRoot "lat" |> List.map (fun i -> i.SplitReason)))
        // The refusal is the ordinary named-axis one (BL3999): the two
        // identities carry DIFFERENT axis names, and a named index type does
        // not co-iterate with a differently-named one merely by matching
        // extent. Ids alone would not do this -- `unify`'s ArrayElem arm never
        // compares them.
        check "axis: the split identities carry different axis NAMES"
            (match axisIdentities axisCoordRoot "lat" with
             | newest :: older :: [] ->
                 newest.IndexType.Tag <> older.IndexType.Tag
                 && (defaultArg newest.IndexType.Tag "").StartsWith (axisTagPrefix + "lat@")
             | _ -> false)
            (sprintf "%A" (axisIdentities axisCoordRoot "lat" |> List.map (fun i -> i.IndexType.Tag)))
        (let r = lower (crossCheckoutSrc axisCoordRoot)
         check "axis: differencing across a REWRITTEN coordinate is REFUSED"
             (refusesOnAxes r)
             (match r with
              | Error e -> e
              | Ok _ -> "the program typechecked, so the two checkouts' lat axes co-iterated"))

        // (d) A regrid: the extent moved, which is a split before any
        // coordinate content is even compared.
        let regridMain = axisModule "ck2" (axisRegridRoot + "@branch:main")
        let regridTag = axisModule "ck1" (axisRegridRoot + "@tag:v1.0")
        check "axis: a REGRID (extent 5 -> 6) splits the identity"
            ((axisIdsOf regridMain).["lat"] <> (axisIdsOf regridTag).["lat"]
             && (axisIdsOf regridMain).["lon"] = (axisIdsOf regridTag).["lon"])
            (sprintf "%A vs %A" (axisIdsOf regridMain) (axisIdsOf regridTag))
        // Both extents in the reason, in the order the two checkouts were
        // loaded -- `main` (6) is read above before `v1.0` (5).
        check "axis: the regrid split names the EXTENT"
            (match axisIdentities axisRegridRoot "lat" with
             | newest :: _ :: [] -> newest.SplitReason = Some "extent 6 -> 5"
             | _ -> false)
            (sprintf "%A" (axisIdentities axisRegridRoot "lat" |> List.map (fun i -> i.SplitReason)))
        (let r = lower (crossCheckoutSrc axisRegridRoot)
         check "axis: differencing across a REGRID is REFUSED"
             (match r with Error _ -> true | Ok _ -> false)
             "the program typechecked, so two differently-sized lat axes unified")

        // (e) No coordinate variable: nothing to compare, so name + extent is
        // the whole identity and a data-only commit still shares.
        let ncMain = axisModule "ck2" (axisNoCoordRoot + "@branch:main")
        let ncTag = axisModule "ck1" (axisNoCoordRoot + "@tag:v1.0")
        check "axis: dims with NO coordinate variable share on name + extent"
            (axisIdsOf ncMain = axisIdsOf ncTag && (axisIdsOf ncMain).Count = 2)
            (sprintf "%A vs %A" (axisIdsOf ncMain) (axisIdsOf ncTag))
        check "axis: a coordinate-less axis records no fingerprint at all"
            (match axisIdentities axisNoCoordRoot "row" with
             | [ one ] -> one.CoordFP.IsNone && one.Extent = 5L
             | _ -> false)
            (sprintf "%A" (axisIdentities axisNoCoordRoot "row" |> List.map (fun i -> (i.CoordFP, i.Extent))))
        (match lower (crossCheckoutSrc axisNoCoordRoot) with
         | Ok _ -> check "axis: differencing across a coordinate-less grid TYPECHECKS" true ""
         | Error e -> check "axis: differencing across a coordinate-less grid TYPECHECKS" false e)

        // (f) A byte-identical SECOND repo. Same dim names, same extents, same
        // coordinate bytes -- and no sharing, because there is no identity
        // between two repos to anchor one.
        let twinM = axisModule "ck3" (axisTwinRoot + "@branch:main")
        check "axis: two DIFFERENT repos never share, however identical"
            ((axisIdsOf twinM).["lat"] <> (axisIdsOf mainM).["lat"]
             && (axisIdsOf twinM).["lon"] <> (axisIdsOf mainM).["lon"]
             && (axisIdsOf twinM).Count = 2)
            (sprintf "%A vs %A" (axisIdsOf twinM) (axisIdsOf mainM))
        check "axis: the twin repo's axes are a separate mint-table entry"
            ((axisIdentities axisTwinRoot "lat").Length = 1
             && (axisIdentities wxRoot "lat").Length = 1)
            (sprintf "twin %d, wx %d"
                 (axisIdentities axisTwinRoot "lat").Length (axisIdentities wxRoot "lat").Length)
        (let crossRepo =
            sprintf """
import icechunk as ic

let r1 = ic.load("%s")
let r2 = ic.load("%s")
let ck1 = r1.checkout("main")
let ck2 = r2.checkout("main")
let A = ck1.vars.temp |> ic.read
let B = ck2.vars.temp |> ic.read
let d = B - A
let total = reduce(d, (+), axes = 2)
"""
                    wxRoot axisTwinRoot
         let r = lower crossRepo
         check "axis: differencing ACROSS REPOS is refused, identical bytes and all"
             (refusesOnAxes r)
             (match r with Error e -> e | Ok _ -> "the program typechecked across two repos"))

        // (g) The table is per COMPILATION. Both resets clear it, and the next
        // compilation mints identities of its own -- ids from a dead table can
        // never come back and be mistaken for live ones.
        let beforeReset = (axisIdsOf (axisModule "ck" mainKey)).["lat"]
        resetAxisMint ()
        check "axis: resetAxisMint clears the table" (axisIdentities wxRoot "lat" = []) ""
        let afterReset = (axisIdsOf (axisModule "ck" mainKey)).["lat"]
        check "axis: the next compilation mints a FRESH identity (no leak)"
            (afterReset <> beforeReset) $"{beforeReset} vs {afterReset}"
        resetCaches ()
        check "axis: resetCaches clears the mint table too" (axisIdentities wxRoot "lat" = []) ""
        let afterCaches = (axisIdsOf (axisModule "ck" mainKey)).["lat"]
        check "axis: and that one is fresh as well"
            (afterCaches <> afterReset && afterCaches <> beforeReset)
            $"{beforeReset}, {afterReset}, {afterCaches}"
     with ex -> check "axis: identity across checkouts" false ex.Message)

    // The differencing program, compiled and RUN: sharing is not a
    // typechecker-only property. tempV2 sums to 2100 and tempV1 to 210, so the
    // difference sums to 1890 -- computed cell by cell over axes both
    // checkouts agree on.
    (try
        let axisE2eRoot = fixRepo "ic_axis_e2e"
        IW.writeRepoAt [ axisE2eRoot; Path.Combine(e2eDir, axisE2eRoot) ] wxFull
        resetCaches ()
        match lower (crossCheckoutSrc axisE2eRoot) with
        | Error e -> check "axis e2e: the cross-checkout difference lowers" false e
        | Ok ir ->
            check "axis e2e: both checkouts' reads reach ProviderReads"
                ((ir.Modules.[0].ProviderReads
                  |> Map.filter (fun _ s -> s.Provider = "icechunk" && s.VarName = "temp")
                  |> Map.count) = 2)
                (sprintf "%d icechunk temp reads" (ir.Modules.[0].ProviderReads |> Map.count))
            let (cppCode, _) = CodeGen.genSelfContainedProgramFromIR ir "icechunk_axis_diff"
            CodeGen.deployRuntimeHeaders e2eDir
            let cppFile = Path.Combine(e2eDir, "icechunk_axis_diff.cpp")
            File.WriteAllText(cppFile, cppCode)
            (match compileCpp cppFile e2eDir with
             | Ok exePath ->
                 (match runExecutable exePath with
                  | Ok (0, runOut) ->
                      check "axis e2e: total = sum(tempV2 - tempV1) = 1890"
                          (match printedScalar "total" runOut with
                           | Some v -> abs (v - 1890.0) <= 1e-9
                           | None -> false)
                          runOut
                  | Ok (code, runOut) -> check "axis e2e: runs (exit 0)" false ($"exit {code}: {runOut}")
                  | Error e -> check "axis e2e: runs (exit 0)" false e)
             | Error e -> baselineFailed "icechunk axis-difference e2e" e)
     with ex -> check "axis e2e: cross-checkout difference" false ex.Message)

    // ---------------------------------------------------------------
    // Summary
    // ---------------------------------------------------------------
    printFooter "Icechunk Provider" [$"{passed} passed"; $"{failed} failed"]
    if failed > 0 then 1 else 0
