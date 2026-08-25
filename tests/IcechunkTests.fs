// Icechunk provider tests. Fully hermetic and, deliberately, PURE: every
// assertion here is about a function whose inputs are bytes, strings or
// synthetic domain values -- no real Icechunk repo is needed, and none is
// committed. The few file-touching cases hand-assemble a 39-byte metadata
// header into a temp directory, which is enough to exercise every refusal
// that fires BEFORE the payload seam (missing repo, not an Icechunk file,
// spec-1 header, wrong file type, unknown compression, object-store URL).
//
// The payload decoder itself is the deferred §6.2 dependency decision
// (docs/plans/plan-icechunk-provider.md), so the two seam stubs -- zstd
// decompression and FlatBuffers payload decode -- are PINNED here by their
// messages: when P1 lands, these two pins are what change.
module Blade.Tests.IcechunkTests

open System
open System.IO
open Blade
open Blade.IR
open Blade.Types
open Blade.IcechunkProvider
open Blade.Tests.TestHarness

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
     check "base32: spec example encodes to 1CECHNKREP0F1RSTCMT0" (s = "1CECHNKREP0F1RSTCMT0") s)

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
    // 5. The deferred seams (§6.2) -- pinned by message
    // ---------------------------------------------------------------
    printfn "\n--- deferred payload seam ---"

    check "seam: decodePayload is an honest stub naming §6.2"
        (decodePayload FtRepoInfo [| 1uy; 2uy |] = Error pendingPayloadDecode)
        (errorText (decodePayload FtRepoInfo [| 1uy; 2uy |]))
    check "seam: the pending message names the plan document"
        (pendingPayloadDecode = "icechunk payload decode pending the §6.2 dependency decision (docs/plans/plan-icechunk-provider.md)")
        pendingPayloadDecode
    check "seam: every file type hits the same stub"
        ([FtSnapshot; FtManifest; FtTransactionLog; FtRepoInfo]
         |> List.forall (fun t -> decodePayload t [||] = Error pendingPayloadDecode)) ""

    check "seam: zstd decompression is an honest stub naming §6.2"
        (decompress [| 1uy |] = Error pendingZstd) (errorText (decompress [| 1uy |]))
    check "seam: compression 0 is the identity (payload passes through unchanged)"
        (decompressPayload CompNone [| 7uy; 8uy; 9uy |] = Ok [| 7uy; 8uy; 9uy |])
        (errorText (decompressPayload CompNone [| 7uy; 8uy; 9uy |]))
    check "seam: compression 1 routes to the zstd stub"
        (decompressPayload CompZstd [| 7uy |] = Error pendingZstd)
        (errorText (decompressPayload CompZstd [| 7uy |]))

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
         let native = Native { File = "chunks/AAA"; Offset = 0L; Length = 48L }
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

    let plainRepo = fakeRepo "plain.icechunk" (mkHeader "icechunk-rust 1.2.3" 2uy 6uy 0uy)
    let zstdRepo = fakeRepo "zstd.icechunk" (mkHeader "icechunk-rust 1.2.3" 2uy 6uy 1uy)
    let spec1Repo = fakeRepo "spec1.icechunk" (mkHeader "icechunk-rust 0.1.0" 1uy 6uy 0uy)
    let wrongTypeRepo = fakeRepo "wrongtype.icechunk" (mkHeader "icechunk-rust" 2uy 1uy 0uy)
    let badCompRepo = fakeRepo "badcomp.icechunk" (mkHeader "icechunk-rust" 2uy 6uy 9uy)
    let emptyDir = Path.Combine(tmp, "empty.icechunk")
    Directory.CreateDirectory emptyDir |> ignore

    check "repo: a well-formed repo header validates (pre-payload, LIVE today)"
        (match validateRepoFile plainRepo with Ok _ -> true | Error _ -> false)
        (errorText (validateRepoFile plainRepo))
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

    // `load` runs every real step and stops at the seam -- with context.
    (let r = load plainRepo
     check "load: a bare path reaches the payload seam (uncompressed)"
         (isError r "payload decode pending") (errorText r))
    (let r = load zstdRepo
     check "load: a compressed repo file reaches the ZSTD seam"
         (isError r "zstd decompression pending") (errorText r))
    (let r = load (plainRepo + "@branch:main")
     check "load: a canonical key reaches the same seam"
         (isError r "payload decode pending") (errorText r))
    (let r = load (plainRepo + "@bogus:main")
     check "load: a malformed key refuses before any file IO"
         (isError r "unknown ref kind") (errorText r))

    // loadAsModule: bare path -> the EMPTY repo-handle module; a refspec ->
    // the pending error (the full dims/vars module lands with the decoder).
    (let m = loadAsModule (IRBuilder()) "wx" plainRepo
     check "module: a bare path binds an EMPTY repo-handle module (no dims, no vars)"
         (m.Name = "wx" && m.Types.IsEmpty && m.Bindings.IsEmpty) $"%A{m.Types}")
    (let msg = caught (fun () -> loadAsModule (IRBuilder()) "wx" spec1Repo)
     check "module: a spec-1 repo fails at the load site, by name"
         (msg.Contains "spec version 1") msg)
    (let msg = caught (fun () -> loadAsModule (IRBuilder()) "ck" (plainRepo + "@tag:v1.0"))
     check "module: a checkout key fails with the pending payload message"
         (msg.Contains "payload decode pending") msg)
    (let msg = caught (fun () -> loadAsModule (IRBuilder()) "ck" (plainRepo + "@bogus:v1"))
     check "module: a malformed key fails loudly" (msg.Contains "malformed store key") msg)

    // Version stamp: mtime of the ONE mutable file, refspec-independent.
    check "stamp: versionStamp reads the repo file's mtime" (versionStamp plainRepo > 0L) $"{versionStamp plainRepo}"
    check "stamp: a refspec does not change the stamp (same repo file)"
        (versionStamp plainRepo = versionStamp (plainRepo + "@branch:main")) ""
    check "stamp: an unreadable repo stamps 0 rather than throwing"
        (versionStamp (Path.Combine(tmp, "nope.icechunk")) = 0L) ""

    check "fingerprint: 64 hex characters" ((fingerprint plainRepo).Length = 64) (fingerprint plainRepo)
    check "fingerprint: stable across calls" (fingerprint plainRepo = fingerprint plainRepo) ""
    check "fingerprint: distinguishes two checkouts of one repo"
        (fingerprint (plainRepo + "@branch:main") <> fingerprint (plainRepo + "@tag:v1.0")) ""
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

    (let msg = caught (fun () -> spec.GenWriteVar plainRepo "temp" "temp" arrType ["t"])
     check "spec: a write refuses loudly, citing writes-as-commits"
         (msg.Contains "COMMIT" && msg.Contains "conditional swap") msg)
    (let msg = caught (fun () -> spec.GenReadVar (Path.Combine(tmp, "nope.icechunk")) "temp" "temp" arrType)
     check "spec: a read of a missing repo names the REPO, not the deferred seam"
         (msg.Contains "does not exist") msg)
    (let msg = caught (fun () -> spec.GenReadVar plainRepo "temp" "temp" arrType)
     check "spec: a read of a real repo file stops at the payload seam"
         (msg.Contains "payload decode pending") msg)

    (let r = spec.ReadVarData (Path.Combine(tmp, "nope.icechunk")) "temp"
     check "spec: the compile-time fold reports the missing repo"
         (isError r "does not exist") (errorText r))
    (let r = spec.ReadVarData plainRepo "temp"
     check "spec: the compile-time fold otherwise stops at the payload seam"
         (isError r "payload decode pending") (errorText r))
    check "spec: VarDimNames is None-safe on an unreadable store"
        ((spec.VarDimNames (Path.Combine(tmp, "nope.icechunk")) "temp").IsNone) ""
    check "spec: VarDimNames is None-safe on a repo handle"
        ((spec.VarDimNames plainRepo "temp").IsNone) ""

    (try Directory.Delete(tmp, true) with _ -> ())

    // ---------------------------------------------------------------
    // Summary
    // ---------------------------------------------------------------
    printFooter "Icechunk Provider" [$"{passed} passed"; $"{failed} failed"]
    if failed > 0 then 1 else 0
