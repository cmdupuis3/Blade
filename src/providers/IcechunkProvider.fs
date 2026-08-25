// Blade Icechunk Store Provider: compile-time metadata extraction from
// Icechunk repos (spec version 2, covering formats 2.0 and 2.1). An Icechunk
// repo is a DIRECTORY of immutable files plus ONE mutable entry point:
//
//     $ROOT/repo            the only mutable file: refs -> snapshots
//     $ROOT/snapshots/      immutable snapshots (the Zarr hierarchy)
//     $ROOT/manifests/      immutable chunk tables
//     $ROOT/chunks/         immutable chunk payloads (raw, headerless)
//     $ROOT/transactions/   transaction logs (PRUNABLE -- never depended on)
//     $ROOT/overwritten/    repo-file backups
//
// Everything but `repo` is immutable, so the compiler resolves ref -> snapshot
// -> manifests -> per-chunk byte ranges ONCE, at compile time, and bakes the
// result: the generated C++ contains no Icechunk logic at all (no FlatBuffers,
// no zstd -- just std::ifstream + offset reads), keeping LinkNeeds = "none".
// Design doc: docs/plans/plan-icechunk-provider.md.
//
// v1 scope (loud, specific refusals outside it -- §9 of the plan):
//   - LOCAL FILESYSTEM repos only. Object-store URLs (s3://, gs://, ...) are
//     refused BY NAME: the runtime is fstream, not an object-store client.
//   - Spec version byte 2 only. Byte 1 is refused by name; anything else is
//     refused as an unknown spec (a spec-3 repo is a new reader, by design).
//   - Virtual chunk refs, repo status Offline, deleted-tag names, ambiguous
//     bare refs, nested groups, writes, .stream and load_compound: each
//     refused by name, never silently degraded.
//   - Array metadata is verbatim `zarr.json` (§2), parsed with the ZARR
//     provider's own `parseArrayMetaV3`, so every Zarr v1 gate (uncompressed
//     single `bytes` codec, little-endian, numeric dtypes, regular chunk
//     grid, the `blade` packed/orbit layout attribute) is inherited verbatim.
//
// THE PAYLOAD SEAM. Icechunk metadata payloads are (usually zstd-compressed)
// FlatBuffers, and Blade.fsproj has zero PackageReferences today. The plan's
// §6.2 dependency decision (hand-rolled FlatBuffers reader vs the NuGet
// package; ZstdSharp.Port for zstd) is DEFERRED, so exactly two functions in
// this file are honest stubs -- `decompress` and `decodePayload`. Everything
// else is written and typed against them: the parsers, the domain model, the
// refusal gates, the ref resolver, the module builder and the ProviderSpec
// are real, and completing P1 replaces those two stubs and nothing else.
// Refusals that need NO payload (missing repo, not an Icechunk file, spec-1
// header, unknown compression, object-store URL, malformed canonical key)
// fire today with their named messages.
module Blade.IcechunkProvider

open System
open System.IO
open Blade.IR

// ---------------------------------------------------------------------------
// The canonical key (plan §3.1)
// ---------------------------------------------------------------------------
//
// The whole provider contract threads ONE `path: string`. Rather than widen
// that signature, `repo.checkout(...)` desugars to a canonical key
//
//     "<repoPath>@<kind>:<name>"      e.g. data/weather.icechunk@branch:main
//
// with <kind> one of branch | tag | snapshot | ? ('?' = the bare form, whose
// namespace the provider resolves by cross-namespace uniqueness). A key with
// no '@' is a bare REPO HANDLE load. That string is what enters every
// path-keyed carrier (ProviderPaths, ProviderRoots, the fold/axis caches,
// ProviderReadSpec.FilePath); this module parses it back.

/// Which namespace a checkout names. `RefBare` is the one-argument
/// `checkout("x")` form: resolved across branches, tags and snapshot ids
/// with a uniqueness demand, never a precedence order.
type RefKind =
    | RefBranch
    | RefTag
    | RefSnapshot
    | RefBare

/// A parsed canonical key: the repo root, plus the refspec when one is present.
type RepoKey = {
    RepoPath: string
    /// None = a bare repo-handle load (no '@' in the key).
    Ref: (RefKind * string) option
}

/// The `<kind>` token as it appears in a canonical key.
let kindToken (k: RefKind) : string =
    match k with
    | RefBranch -> "branch"
    | RefTag -> "tag"
    | RefSnapshot -> "snapshot"
    | RefBare -> "?"

/// The surface marker constant that names this namespace (plan §3).
let kindMarker (k: RefKind) : string =
    match k with
    | RefBranch -> "ic.branch"
    | RefTag -> "ic.tag"
    | RefSnapshot -> "ic.snapshot"
    | RefBare -> "(bare)"

let private kindOfToken (tok: string) : RefKind option =
    match tok with
    | "branch" -> Some RefBranch
    | "tag" -> Some RefTag
    | "snapshot" -> Some RefSnapshot
    | "?" -> Some RefBare
    | _ -> None

/// Parse a canonical key. A key with no '@' is a bare repo path; otherwise
/// everything after the LAST '@' must be "<kind>:<name>" with a known kind --
/// a malformed suffix is a loud error, never a silently-mistaken path.
let parseKey (key: string) : Result<RepoKey, string> =
    if String.IsNullOrWhiteSpace key then
        Error "icechunk: empty store path (expected a repo directory, optionally suffixed \"@<kind>:<name>\")"
    else
        let at = key.LastIndexOf '@'
        if at < 0 then Ok { RepoPath = key; Ref = None }
        else
            let repoPath = key.Substring(0, at)
            let refspec = key.Substring(at + 1)
            let colon = refspec.IndexOf ':'
            if repoPath = "" then
                Error $"icechunk: malformed store key '{key}' -- the repo path before '@' is empty (expected \"<repoPath>@<kind>:<name>\")"
            elif colon < 0 then
                Error $"icechunk: malformed store key '{key}' -- the text after '@' must be \"<kind>:<name>\" with <kind> one of branch, tag, snapshot or ? (got '{refspec}')"
            else
                let tok = refspec.Substring(0, colon)
                let name = refspec.Substring(colon + 1)
                match kindOfToken tok with
                | None ->
                    Error $"icechunk: malformed store key '{key}' -- unknown ref kind '{tok}' (expected branch, tag, snapshot or ?)"
                | Some _ when name = "" ->
                    Error $"icechunk: malformed store key '{key}' -- the ref name after ':' is empty"
                | Some kind -> Ok { RepoPath = repoPath; Ref = Some (kind, name) }

/// Render a canonical key (the inverse of `parseKey` on well-formed input).
let formatKey (key: RepoKey) : string =
    match key.Ref with
    | None -> key.RepoPath
    | Some (kind, name) -> $"{key.RepoPath}@{kindToken kind}:{name}"

/// Object-store schemes refused by name: v1 emits pure std::ifstream C++,
/// with no object-store client at compile time OR run time.
let private urlSchemes =
    [ "s3://"; "s3a://"; "gs://"; "gcs://"; "az://"; "abfs://"; "abfss://"; "r2://"; "http://"; "https://" ]

/// Gate a repo path to the local filesystem (§9). LIVE today.
let checkLocalPath (repoPath: string) : Result<unit, string> =
    match urlSchemes |> List.tryFind (fun s -> repoPath.StartsWith(s, StringComparison.OrdinalIgnoreCase)) with
    | Some s ->
        Error $"icechunk repo '{repoPath}': object-store URLs ('{s}') are not supported -- v1 reads LOCAL FILESYSTEM repos only, because the generated program reads chunks with std::ifstream and links no object-store client; mirror the repo to local disk first"
    | None -> Ok ()

// ---------------------------------------------------------------------------
// Crockford base32 (object ids -> file names)
// ---------------------------------------------------------------------------
//
// Ids are Crockford base32, UPPERCASE, no padding, zero bits padding the last
// group on the right: a 12-byte object id (snapshots, manifests, chunks) is
// 20 characters, an 8-byte node id is 13. The alphabet omits I, L, O and U.

/// Crockford's encoding alphabet (index = 5-bit value).
let base32Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ"

/// Characters in a 12-byte object id (snapshot / manifest / chunk names).
let objectIdChars = 20

/// Characters in an 8-byte node id.
let nodeIdChars = 13

/// Crockford base32 of a byte string: MSB-first, five bits per character,
/// the final partial group right-padded with zero bits. No '=' padding.
let base32Encode (bytes: byte[]) : string =
    let sb = Text.StringBuilder()
    let mutable acc = 0
    let mutable bits = 0
    for b in bytes do
        acc <- (acc <<< 8) ||| int b
        bits <- bits + 8
        while bits >= 5 do
            bits <- bits - 5
            sb.Append(base32Alphabet.[(acc >>> bits) &&& 31]) |> ignore
    if bits > 0 then
        sb.Append(base32Alphabet.[(acc <<< (5 - bits)) &&& 31]) |> ignore
    sb.ToString()

/// Is `s` shaped like an object id (20 canonical Crockford characters)? The
/// bare-ref resolver uses this to decide whether a name is even a PLAUSIBLE
/// snapshot id before comparing it against the repo's snapshot list.
let isObjectIdForm (s: string) : bool =
    s.Length = objectIdChars && s |> Seq.forall (fun c -> base32Alphabet.IndexOf c >= 0)

// ---------------------------------------------------------------------------
// Repo layout
// ---------------------------------------------------------------------------

/// The one mutable file in a repo: $ROOT/repo.
let repoFilePath (root: string) : string = Path.Combine(root, "repo")

/// $ROOT/snapshots/<id>, the immutable snapshot named by a 12-byte object id.
let snapshotPath (root: string) (id: byte[]) : string =
    Path.Combine(root, "snapshots", base32Encode id)

/// $ROOT/manifests/<id>.
let manifestPath (root: string) (id: byte[]) : string =
    Path.Combine(root, "manifests", base32Encode id)

/// $ROOT/chunks/<id> -- raw, headerless chunk bytes exactly as the Zarr codec
/// pipeline produced them (§2); readers honor the ref's offset and length.
let chunkPath (root: string) (id: byte[]) : string =
    Path.Combine(root, "chunks", base32Encode id)

// ---------------------------------------------------------------------------
// The 39-byte metadata file header (plan §2)
// ---------------------------------------------------------------------------
//
//   [0 .. 11]   magic, 12 bytes: UTF-8 of "ICE" + U+1F9CA (ice cube) + "CHUNK"
//   [12 .. 35]  implementation name, 24 bytes, space-padded
//   [36]        spec version   (1 or 2; 2.0 and 2.1 share byte 2)
//   [37]        file type      (Snapshot 1, Manifest 2, Attributes 3,
//                               TransactionLog 4, Chunk 5, RepoInfo 6)
//   [38]        compression    (0 none, 1 zstd)
//
// then the payload: a (usually zstd-compressed) FlatBuffer.

/// Bytes of the header, before any payload.
let headerSize = 39

/// The magic, spelled as BYTES rather than as a source string literal so no
/// source-encoding accident can change it: UTF-8 for "ICE" + U+1F9CA + "CHUNK".
let magicBytes : byte[] =
    [| 0x49uy; 0x43uy; 0x45uy                      // I C E
       0xF0uy; 0x9Fuy; 0xA7uy; 0x8Auy              // U+1F9CA, 4-byte UTF-8
       0x43uy; 0x48uy; 0x55uy; 0x4Euy; 0x4Buy |]   // C H U N K

/// Bytes of the space-padded implementation-name field.
let implNameSize = 24

/// The spec version this reader accepts. Formats 2.0 and 2.1 are NOT
/// distinguished by the byte (2.1 only adds one optional table field).
let supportedSpecVersion = 2

/// Which kind of metadata file this is. `FtUnknown` is tolerated at parse so
/// the diagnostic can NAME the byte instead of dying on it; consumers that
/// require a particular kind check for it explicitly.
type FileType =
    | FtSnapshot
    | FtManifest
    | FtAttributes
    | FtTransactionLog
    | FtChunk
    | FtRepoInfo
    | FtUnknown of int

/// Human-readable file type, for diagnostics.
let fileTypeName (t: FileType) : string =
    match t with
    | FtSnapshot -> "Snapshot"
    | FtManifest -> "Manifest"
    | FtAttributes -> "Attributes"
    | FtTransactionLog -> "TransactionLog"
    | FtChunk -> "Chunk"
    | FtRepoInfo -> "RepoInfo"
    | FtUnknown b -> $"unknown file type {b}"

let private fileTypeOfByte (b: byte) : FileType =
    match int b with
    | 1 -> FtSnapshot
    | 2 -> FtManifest
    | 3 -> FtAttributes
    | 4 -> FtTransactionLog
    | 5 -> FtChunk
    | 6 -> FtRepoInfo
    | n -> FtUnknown n

/// Payload compression. Unknown bytes are REFUSED (never assumed to be raw:
/// handing a compressed FlatBuffer to the decoder would fail obscurely).
type Compression =
    | CompNone
    | CompZstd

let compressionName (c: Compression) : string =
    match c with
    | CompNone -> "none"
    | CompZstd -> "zstd"

/// A parsed 39-byte metadata header.
type FileHeader = {
    /// The 24-byte implementation-name field with its space padding removed.
    Implementation: string
    /// Spec version byte (always `supportedSpecVersion` once parsed).
    SpecVersion: int
    FileType: FileType
    Compression: Compression
}

let private hexOf (bytes: byte[]) : string =
    bytes |> Array.map (sprintf "%02x") |> String.concat " "

/// Parse the 39-byte header. Pure: byte array in, Result out. `where_` names
/// the file for the diagnostic ("repo file 'data/w.icechunk/repo'").
///
/// Refusals, all LIVE today: short file, wrong magic, spec version 1 (by
/// name), unknown spec version, unknown compression byte.
let parseHeader (where_: string) (bytes: byte[]) : Result<FileHeader, string> =
    if isNull (box bytes) || bytes.Length < headerSize then
        let n = if isNull (box bytes) then 0 else bytes.Length
        Error $"{where_}: {n} bytes is shorter than the {headerSize}-byte Icechunk metadata header -- this is not an Icechunk metadata file"
    else
        let magic = Array.sub bytes 0 magicBytes.Length
        if magic <> magicBytes then
            Error $"{where_}: leading bytes are {hexOf magic}, not the Icechunk magic {hexOf magicBytes} (UTF-8 \"ICE\" + U+1F9CA + \"CHUNK\") -- this is not an Icechunk metadata file"
        else
            let implName =
                Text.Encoding.UTF8.GetString(bytes, magicBytes.Length, implNameSize).TrimEnd(' ')
            let specVersion = int bytes.[magicBytes.Length + implNameSize]
            let fileType = fileTypeOfByte bytes.[magicBytes.Length + implNameSize + 1]
            let compByte = int bytes.[magicBytes.Length + implNameSize + 2]
            if specVersion = 1 then
                Error $"{where_}: Icechunk spec version 1 is not supported -- this reader accepts spec version {supportedSpecVersion} only (which covers formats 2.0 and 2.1); rewrite the repo with an icechunk release that emits spec 2"
            elif specVersion <> supportedSpecVersion then
                Error $"{where_}: unknown Icechunk spec version {specVersion} -- this reader accepts spec version {supportedSpecVersion} only (formats 2.0 and 2.1); a newer spec byte means a newer reader, by design"
            else
                match compByte with
                | 0 | 1 ->
                    Ok { Implementation = implName
                         SpecVersion = specVersion
                         FileType = fileType
                         Compression = (if compByte = 0 then CompNone else CompZstd) }
                | n ->
                    Error $"{where_}: unknown compression byte {n} -- Icechunk defines 0 (none) and 1 (zstd)"

// ---------------------------------------------------------------------------
// Domain model (mirrors the spec §2 tables; filled by the payload decoder)
// ---------------------------------------------------------------------------

/// Repo availability, from the repo file's RepoStatus. Online and ReadOnly
/// both read; Offline refuses (§9).
type RepoStatus =
    | StatusOnline
    | StatusReadOnly
    | StatusOffline

let statusName (s: RepoStatus) : string =
    match s with
    | StatusOnline -> "Online"
    | StatusReadOnly -> "ReadOnly"
    | StatusOffline -> "Offline"

/// One entry of the repo file's `snapshots: [SnapshotInfo]` list.
type SnapshotInfo = {
    /// 12-byte object id; `base32Encode` gives the snapshots/ file name.
    Id: byte[]
    /// The schema's `parent_offset`: the parent snapshot's position in this
    /// same list. Whether it is an absolute index or a distance back is
    /// pinned when the payload decoder lands (§6.2) -- the READ path never
    /// walks ancestry (§5.2 rejects tx-log/ancestry mechanisms outright).
    ParentOffset: int
    /// Commit time, Unix milliseconds.
    FlushedAtMillis: int64
    /// Commit message.
    Message: string
}

/// The repo file (`Repo` root table): the only mutable state in a repo.
/// Branches and tags are SEPARATE namespaces -- a branch `x` and a tag `x`
/// can coexist, which is why bare refs demand uniqueness rather than picking
/// a winner.
type RepoInfo = {
    /// (name, index into `Snapshots`).
    Branches: (string * int) list
    /// (name, index into `Snapshots`).
    Tags: (string * int) list
    /// Tombstones: a deleted tag name must never be recreated, so a name
    /// listed here names its tombstone and nothing else.
    DeletedTags: string list
    Snapshots: SnapshotInfo list
    Status: RepoStatus
}

/// A node is either a group or an array; v1 reads ROOT-LEVEL arrays only.
type NodeKind =
    | NodeGroup
    | NodeArray

/// One `NodeSnapshot` entry of a snapshot (the entries are sorted by path).
type NodeMeta = {
    /// 8-byte node id, STABLE across snapshots for the node's lifetime --
    /// the anchor of §5.2's axis-identity test. Delete-then-recreate mints a
    /// fresh id, which is exactly why the test is sound.
    Id: byte[]
    /// Hierarchy path, e.g. "/temp". The root group is "/".
    Path: string
    Kind: NodeKind
    /// The array's `zarr.json` VERBATIM, as UTF-8 JSON text: shape, dtype,
    /// codecs, dimension_names, attributes (so the `blade` packed-layout
    /// attribute rides along unchanged). Parsed with the Zarr provider's own
    /// `parseArrayMetaV3`, inheriting every v1 gate.
    UserDataJson: string
    /// Dimension names as stored STRUCTURALLY in ArrayNodeData -- cross-checked
    /// against the JSON's `dimension_names` (loud on disagreement, §6.1).
    DimensionNames: string list option
    /// Per manifest holding some of this array's chunks: the manifest's
    /// 12-byte object id and, per dimension, the half-open chunk-index range
    /// [from, to) it covers. Ranges across manifests must not overlap -- each
    /// chunk coordinate is covered by at most one manifest (§2).
    ManifestRefs: (byte[] * (int64 * int64) list) list
}

/// One resolved snapshot: its id plus its nodes, sorted by path.
type Snapshot = {
    Id: byte[]
    Nodes: NodeMeta list
}

/// Where one chunk's bytes live (plan §6.1). VIRTUAL refs -- an external
/// file/URL with offset/length -- have no case here on purpose: they are
/// refused BY NAME at parse (`virtualChunkRefused`), because honoring one
/// would mean the emitted C++ reads a file outside the repo.
type ChunkLoc =
    | Fill
    | Inline of byte[]
    | Native of NativeChunk

/// A native chunk ref: a byte range of a file under $ROOT/chunks/. The
/// reference writer emits one chunk per file (offset 0), but the schema
/// permits packing, so readers must honor offset and length.
and NativeChunk = {
    File: string
    Offset: int64
    Length: int64
}

/// The named refusal for a virtual chunk ref (§9). The decoder calls this
/// rather than inventing a ChunkLoc case, so the refusal has one wording.
let virtualChunkRefused (arrayName: string) (location: string) : string =
    $"icechunk array '{arrayName}': VIRTUAL chunk refs are not supported in v1 (this chunk points at '{location}', a file outside the repo) -- the emitted reader only opens files under the repo's chunks/ directory; rewrite the virtual refs into native chunks, or read the referenced store directly"

/// One array's chunk table inside a manifest (`ArrayManifest`); entries are
/// sorted lexicographically by `Index`.
type ArrayManifest = {
    /// The 8-byte node id this table belongs to.
    NodeId: byte[]
    Refs: ChunkRef list
}

/// One `ChunkRef`: a plain chunk-grid coordinate vector plus its location.
and ChunkRef = {
    Index: int64 list
    Loc: ChunkLoc
}

/// What a decoded metadata payload turned out to be. One case per file type
/// the read path consumes; transaction logs are decoded by nothing today
/// (they are PRUNABLE under 2.1, so no read path may depend on them).
type Payload =
    | PRepoInfo of RepoInfo
    | PSnapshot of Snapshot
    | PManifest of ArrayManifest list
    | PTransactionLog

let payloadKindName (p: Payload) : string =
    match p with
    | PRepoInfo _ -> "RepoInfo"
    | PSnapshot _ -> "Snapshot"
    | PManifest _ -> "Manifest"
    | PTransactionLog -> "TransactionLog"

// ---------------------------------------------------------------------------
// THE DEFERRED SEAM (plan §6.2) -- the only two stubs in this file
// ---------------------------------------------------------------------------

/// What a user sees today wherever a payload would be needed. P1's completion
/// replaces `decodePayload`'s body and this message goes away with it.
let pendingPayloadDecode =
    "icechunk payload decode pending the §6.2 dependency decision (docs/plans/plan-icechunk-provider.md)"

/// The zstd half of the same decision (real writers compress metadata, so
/// this is unavoidable either way -- see the §6.2 option table).
let pendingZstd =
    "icechunk zstd decompression pending the §6.2 dependency decision (docs/plans/plan-icechunk-provider.md)"

/// The step ABOVE the payload seam: turning a resolved chunk table into
/// bytes rides the shared chunk-source core (plan §7 / phase P0).
let pendingChunkSource =
    "icechunk chunk assembly pending the shared ChunkSource seam (docs/plans/plan-icechunk-provider.md §7, phase P0)"

/// SEAM: zstd decompression of a metadata payload. A named stub, NOT a
/// silent pass-through -- returning the compressed bytes would hand the
/// decoder garbage and fail somewhere far away from the cause.
let decompress (bytes: byte[]) : Result<byte[], string> =
    ignore bytes
    Error pendingZstd

/// Post-header bytes -> plaintext payload bytes, per the header's compression
/// byte. Identity for compression 0; the `decompress` seam for compression 1.
let decompressPayload (compression: Compression) (bytes: byte[]) : Result<byte[], string> =
    match compression with
    | CompNone -> Ok bytes
    | CompZstd -> decompress bytes

/// SEAM: post-header, post-decompression bytes -> domain values. The bytes
/// are a FlatBuffer whose root table is chosen by the file type. Everything
/// downstream of this function is written and typed against it; completing
/// P1 replaces this body and nothing else in the file.
let decodePayload (fileType: FileType) (bytes: byte[]) : Result<Payload, string> =
    ignore fileType
    ignore bytes
    Error pendingPayloadDecode

// ---------------------------------------------------------------------------
// Ref resolution (pure: RepoInfo in, snapshot id out)
// ---------------------------------------------------------------------------

let private refNames (rs: (string * int) list) : string =
    if List.isEmpty rs then "(none)" else rs |> List.map fst |> String.concat ", "

let private tombstoneMessage (name: string) : string =
    $"icechunk: '{name}' is a DELETED TAG -- it is listed in the repo's deleted_tags tombstones, and a deleted tag name is never recreated, so it names nothing; pick another ref"

/// Offline refuses; Online and ReadOnly proceed (§9).
let private statusGate (info: RepoInfo) : Result<unit, string> =
    match info.Status with
    | StatusOffline ->
        Error "icechunk: repo status is Offline -- the repo is marked unavailable, so no ref resolves from it (Online and ReadOnly repos both read normally)"
    | StatusOnline | StatusReadOnly -> Ok ()

let private snapshotIdAt (info: RepoInfo) (what: string) (name: string) (idx: int) : Result<byte[], string> =
    if idx < 0 || idx >= info.Snapshots.Length then
        Error $"icechunk repo file is inconsistent: {what} '{name}' points at snapshot index {idx}, but the repo lists {info.Snapshots.Length} snapshots"
    else
        let s = List.item idx info.Snapshots
        Ok s.Id

/// Resolve a refspec against a parsed repo file. Pure and total: the only
/// inputs are the parsed `repo` payload and the refspec, so every refusal
/// here is unit-testable without a repo on disk.
///
/// `RefBare` searches branches, tags and (when the name is object-id shaped)
/// snapshot ids, and demands UNIQUENESS: zero hits lists the repo's actual
/// branches and tags, two hits names both namespaces and points at the
/// `ic.branch` / `ic.tag` / `ic.snapshot` markers. There is deliberately no
/// precedence order between namespaces.
let resolveRef (info: RepoInfo) (kind: RefKind) (name: string) : Result<byte[], string> =
    statusGate info
    |> Result.bind (fun () ->
        let branchHit = info.Branches |> List.tryFind (fun (n, _) -> n = name)
        let tagHit = info.Tags |> List.tryFind (fun (n, _) -> n = name)
        let snapHit =
            if isObjectIdForm name then
                info.Snapshots |> List.tryFind (fun s -> base32Encode s.Id = name)
            else None
        let deleted = info.DeletedTags |> List.contains name
        match kind with
        | RefBranch ->
            match branchHit with
            | Some (_, idx) -> snapshotIdAt info "branch" name idx
            | None ->
                Error $"icechunk: no branch named '{name}' -- branches in this repo: {refNames info.Branches}"
        | RefTag ->
            match tagHit with
            | Some (_, idx) -> snapshotIdAt info "tag" name idx
            | None when deleted -> Error (tombstoneMessage name)
            | None ->
                Error $"icechunk: no tag named '{name}' -- tags in this repo: {refNames info.Tags}"
        | RefSnapshot ->
            match snapHit with
            | Some s -> Ok s.Id
            | None when not (isObjectIdForm name) ->
                Error $"icechunk: '{name}' is not a snapshot id -- snapshot ids are exactly {objectIdChars} Crockford base32 characters (0-9 and A-Z without I, L, O, U), e.g. 1CECHNKREP0F1RSTCMT0"
            | None ->
                Error $"icechunk: no snapshot '{name}' in this repo -- the repo lists {info.Snapshots.Length} snapshots"
        | RefBare ->
            let hits =
                [ (match branchHit with
                   | Some (_, idx) -> Some ("branch", snapshotIdAt info "branch" name idx)
                   | None -> None)
                  (match tagHit with
                   | Some (_, idx) -> Some ("tag", snapshotIdAt info "tag" name idx)
                   | None -> None)
                  (match snapHit with
                   | Some s -> Some ("snapshot", Ok s.Id)
                   | None -> None) ]
                |> List.choose id
            match hits with
            | [ (_, resolved) ] -> resolved
            | [] when deleted -> Error (tombstoneMessage name)
            | [] ->
                Error $"icechunk: no branch, tag or snapshot named '{name}' -- branches: {refNames info.Branches}; tags: {refNames info.Tags}"
            | many ->
                let kinds = many |> List.map fst
                let kindsText = kinds |> String.concat " AND a "
                let markers = kinds |> List.map (fun k -> $"ic.{k}") |> String.concat " / "
                let firstKind = List.head kinds
                Error $"icechunk: '{name}' is ambiguous -- it names a {kindsText} in this repo, and branches, tags and snapshots are separate namespaces with no precedence order between them. Name the namespace with a marker ({markers}): e.g. checkout(\"{name}\", ic.{firstKind}), or the canonical key form '@{firstKind}:{name}'")

// ---------------------------------------------------------------------------
// Reading metadata files (header REAL today; payload at the seam)
// ---------------------------------------------------------------------------

/// The first `headerSize` bytes of a file (short reads tolerated: a truncated
/// file must reach `parseHeader`, which names the truncation).
let private readHeaderBytes (file: string) : byte[] =
    use fs = File.OpenRead file
    let buf = Array.zeroCreate<byte> headerSize
    let mutable off = 0
    let mutable n = 1
    while off < headerSize && n > 0 do
        n <- fs.Read(buf, off, headerSize - off)
        off <- off + n
    Array.sub buf 0 off

/// Header-only validation of an existing metadata file. Everything decidable
/// from 39 bytes: magic, spec version, file type, compression byte. LIVE.
let private validateFileHeader (where_: string) (file: string) (expected: FileType) : Result<FileHeader, string> =
    try
        parseHeader where_ (readHeaderBytes file)
        |> Result.bind (fun h ->
            if h.FileType <> expected then
                Error $"{where_}: file type is {fileTypeName h.FileType}, expected {fileTypeName expected} -- '{file}' is not the file it is being read as"
            else Ok h)
    with ex ->
        Error $"{where_}: cannot read '{file}': {ex.Message}"

/// Pre-payload validation of a repo root: the path is local, the directory
/// exists, `$ROOT/repo` exists, and its 39-byte header is a spec-2 RepoInfo
/// header with a known compression byte. EVERY refusal here fires today --
/// this is what a bare `ic.load(path)` runs.
let validateRepoFile (root: string) : Result<FileHeader, string> =
    checkLocalPath root
    |> Result.bind (fun () ->
        let file = repoFilePath root
        if not (Directory.Exists root) then
            Error $"icechunk repo '{root}' does not exist (an Icechunk repo is a DIRECTORY holding 'repo' plus snapshots/, manifests/ and chunks/)"
        elif not (File.Exists file) then
            Error $"'{root}' is not an Icechunk repo: there is no 'repo' file at '{file}' (the repo file is the entry point and the only mutable object in a repo)"
        else
            validateFileHeader $"icechunk repo file '{file}'" file FtRepoInfo)

/// Read a metadata file whole: header (real), file-type check (real),
/// decompression (identity, or the zstd seam), payload bytes out.
let private readMetadataFile (where_: string) (file: string) (expected: FileType) : Result<FileHeader * byte[], string> =
    let raw =
        try Ok (File.ReadAllBytes file)
        with ex -> Error $"{where_}: cannot read '{file}': {ex.Message}"
    raw
    |> Result.bind (fun bytes ->
        parseHeader where_ bytes
        |> Result.bind (fun h ->
            if h.FileType <> expected then
                Error $"{where_}: file type is {fileTypeName h.FileType}, expected {fileTypeName expected} -- '{file}' is not the file it is being read as"
            else
                decompressPayload h.Compression (Array.sub bytes headerSize (bytes.Length - headerSize))
                |> Result.map (fun payload -> (h, payload))))

// ---------------------------------------------------------------------------
// Handles: a repo, and a checkout of one snapshot of it
// ---------------------------------------------------------------------------

/// A parsed repo root. `ic.load(path)` with a bare path binds one of these
/// (as an EMPTY provider module: no dims, no vars -- checkout is the factory).
type RepoHandle = {
    Root: string
    Header: FileHeader
    Info: RepoInfo
}

/// One resolved checkout: the snapshot a refspec named, plus its nodes. This
/// is what `LoadAsModule` turns into a dims/vars module.
type CheckoutHandle = {
    Repo: RepoHandle
    Ref: RefKind * string
    SnapshotId: byte[]
    Snapshot: Snapshot
}

/// What a canonical key loaded to: a bare repo handle, or a checkout.
type Loaded =
    | LoadedRepo of RepoHandle
    | LoadedCheckout of CheckoutHandle

/// Read and decode `$ROOT/repo`. Header REAL, payload at the seam.
let readRepoHandle (root: string) : Result<RepoHandle, string> =
    checkLocalPath root
    |> Result.bind (fun () -> validateRepoFile root |> Result.map ignore)
    |> Result.bind (fun () ->
        let file = repoFilePath root
        readMetadataFile $"icechunk repo file '{file}'" file FtRepoInfo)
    |> Result.bind (fun (header, payload) ->
        decodePayload header.FileType payload
        |> Result.bind (fun decoded ->
            match decoded with
            | PRepoInfo info -> Ok { Root = root; Header = header; Info = info }
            | other ->
                Error $"icechunk repo file '{repoFilePath root}' decoded as a {payloadKindName other} table, not a RepoInfo"))

/// Read and decode `$ROOT/snapshots/<id>`.
let readSnapshot (root: string) (snapshotId: byte[]) : Result<Snapshot, string> =
    let file = snapshotPath root snapshotId
    let where_ = $"icechunk snapshot '{base32Encode snapshotId}'"
    if not (File.Exists file) then
        Error $"{where_}: '{file}' is missing -- the snapshot a ref names must exist; an expired/garbage-collected snapshot cannot be read"
    else
        readMetadataFile where_ file FtSnapshot
        |> Result.bind (fun (header, payload) ->
            decodePayload header.FileType payload
            |> Result.bind (fun decoded ->
                match decoded with
                | PSnapshot snap -> Ok snap
                | other -> Error $"{where_} decoded as a {payloadKindName other} table, not a Snapshot"))

/// Read and decode `$ROOT/manifests/<id>`.
let readManifest (root: string) (manifestId: byte[]) : Result<ArrayManifest list, string> =
    let file = manifestPath root manifestId
    let where_ = $"icechunk manifest '{base32Encode manifestId}'"
    if not (File.Exists file) then
        Error $"{where_}: '{file}' is missing -- manifests are immutable and must outlive every snapshot referencing them"
    else
        readMetadataFile where_ file FtManifest
        |> Result.bind (fun (header, payload) ->
            decodePayload header.FileType payload
            |> Result.bind (fun decoded ->
                match decoded with
                | PManifest arrays -> Ok arrays
                | other -> Error $"{where_} decoded as a {payloadKindName other} table, not a Manifest"))

/// Open a canonical key: parse it, read the repo file, and (when the key
/// carries a refspec) resolve the ref and read its snapshot. Every step above
/// the payload seam is real; today the seam is where this stops, so the error
/// a user sees is the pending message WITH the path and ref context.
let load (path: string) : Result<Loaded, string> =
    parseKey path
    |> Result.bind (fun key ->
        readRepoHandle key.RepoPath
        |> Result.bind (fun repo ->
            match key.Ref with
            | None -> Ok (LoadedRepo repo)
            | Some (kind, name) ->
                resolveRef repo.Info kind name
                |> Result.mapError (fun e -> $"{e} (repo '{key.RepoPath}')")
                |> Result.bind (fun snapshotId ->
                    readSnapshot key.RepoPath snapshotId
                    |> Result.map (fun snap ->
                        LoadedCheckout {
                            Repo = repo
                            Ref = (kind, name)
                            SnapshotId = snapshotId
                            Snapshot = snap }))))

// ---------------------------------------------------------------------------
// Arrays inside a checkout
// ---------------------------------------------------------------------------

let private isValidIdent (s: string) =
    s <> ""
    && (Char.IsLetter s.[0] || s.[0] = '_')
    && s |> Seq.forall (fun c -> Char.IsLetterOrDigit c || c = '_')

/// The Blade variable name of a node path. v1 mirrors the Zarr provider's
/// one-level rule: ROOT-LEVEL arrays only, path "/name" with `name` a valid
/// identifier. Deeper groups refuse BY NAME (§6.1). Pure.
let nodeVarName (path: string) : Result<string, string> =
    if path = "" || path.[0] <> '/' then
        Error $"icechunk node path '{path}' is not absolute (paths start at the root group, '/')"
    elif path = "/" then
        Error "icechunk node path '/' is the root group, not an array"
    else
        let rest = path.Substring 1
        if rest.Contains "/" then
            Error $"icechunk array '{path}' lives in a NESTED GROUP -- v1 reads root-level arrays only (paths of the form '/name'); flatten the hierarchy, or read the group as its own store"
        elif not (isValidIdent rest) then
            Error $"icechunk array '{path}': '{rest}' is not a valid Blade identifier"
        else Ok rest

/// Root-level array names in a checkout, in snapshot order.
let arrayNames (ck: CheckoutHandle) : string list =
    ck.Snapshot.Nodes
    |> List.filter (fun n -> n.Kind = NodeArray)
    |> List.choose (fun n -> match nodeVarName n.Path with Ok nm -> Some nm | Error _ -> None)

/// Find a variable in a checkout and parse its `zarr.json` user data with the
/// ZARR provider's v3 parser -- the JSON is verbatim `zarr.json` (§2), so
/// every Zarr v1 gate applies unchanged. A name that exists only inside a
/// nested group is refused BY NAME rather than reported as missing.
let findArray (ck: CheckoutHandle) (varName: string) : Result<NodeMeta * ZarrProvider.ZarrArrayMeta, string> =
    let wanted = "/" + varName
    match ck.Snapshot.Nodes |> List.tryFind (fun n -> n.Path = wanted && n.Kind = NodeArray) with
    | Some node ->
        // The array dir is meaningless for Icechunk (chunks live under
        // $ROOT/chunks by object id, not beside the metadata), so the repo
        // root stands in: parseArrayMetaV3 only uses it for chunk-key paths,
        // which this provider never takes.
        ZarrProvider.parseArrayMetaV3 varName ck.Repo.Root node.UserDataJson
        |> Result.mapError (fun e -> $"icechunk array '{node.Path}': {e}")
        |> Result.map (fun meta -> (node, meta))
    | None ->
        match ck.Snapshot.Nodes |> List.tryFind (fun n -> n.Path.EndsWith("/" + varName)) with
        | Some nested ->
            Error (match nodeVarName nested.Path with
                   | Error e -> e
                   | Ok _ -> $"icechunk array '{nested.Path}' is not a root-level array")
        | None ->
            let names = arrayNames ck
            let listing = if List.isEmpty names then "(none)" else String.concat ", " names
            Error $"variable '{varName}' not found in icechunk snapshot {base32Encode ck.SnapshotId} -- root-level arrays: {listing}"

let private coordText (c: int64 list) : string =
    "[" + (c |> List.map string |> String.concat ", ") + "]"

/// Union an array's manifests into ONE chunk table in row-major chunk-grid
/// order: entry `i` locates the chunk at `gridCoords`[i] (§6.1). A coordinate
/// no manifest covers reads as the array's fill value, per Zarr semantics.
///
/// Loud on every way the manifests can disagree with the metadata: a
/// wrong-rank index vector, a coordinate outside the grid, or two manifests
/// covering the same chunk (§2 requires non-overlapping ChunkIndexRanges).
let buildChunkTable (meta: ZarrProvider.ZarrArrayMeta) (manifests: ArrayManifest list) : Result<ChunkLoc[], string> =
    let lens = ZarrProvider.gridDims meta.Shape meta.Chunks |> List.map int
    let strides = ZarrProvider.rowMajorStrides lens
    let total = lens |> List.fold (*) 1
    let rank = lens.Length
    let table = Array.create total Fill
    let filled = Array.create total false
    let mutable err : string option = None
    for m in manifests do
        for r in m.Refs do
            if err.IsNone then
                if r.Index.Length <> rank then
                    err <- Some $"icechunk manifest for '{meta.Name}': chunk index {coordText r.Index} is rank {r.Index.Length}, but the array's chunk grid is rank {rank}"
                elif List.exists2 (fun (c: int64) (n: int) -> c < 0L || c >= int64 n) r.Index lens then
                    err <- Some $"icechunk manifest for '{meta.Name}': chunk index {coordText r.Index} is outside the chunk grid {coordText (lens |> List.map int64)}"
                else
                    let flat = List.fold2 (fun acc (c: int64) (s: int) -> acc + int c * s) 0 r.Index strides
                    if filled.[flat] then
                        err <- Some $"icechunk manifests for '{meta.Name}' OVERLAP: chunk {coordText r.Index} is covered by more than one manifest (§2 requires each chunk coordinate to be covered at most once)"
                    else
                        filled.[flat] <- true
                        table.[flat] <- r.Loc
    match err with
    | Some e -> Error e
    | None -> Ok table

// ---------------------------------------------------------------------------
// Compile-time payload read (the static fold)
// ---------------------------------------------------------------------------

/// Whole-variable read for `let static A = ic.read(ck.vars.A)`. Structured
/// through the real gates -- key parse, repo file, ref resolution, snapshot,
/// node lookup, zarr.json parse, the packed-layout steering the Zarr provider
/// applies -- and ends at the chunk-source seam.
let readVarData (path: string) (varName: string) : Result<Blade.ProviderRegistry.ProviderVarData, string> =
    match load path with
    | Error e -> Error e
    | Ok (LoadedRepo _) ->
        Error $"'{path}' is an icechunk REPO HANDLE, not a checkout -- a repo handle has no variables; check a ref out first (`repo.checkout(\"main\")`, whose canonical key is '{path}@branch:main')"
    | Ok (LoadedCheckout ck) ->
        findArray ck varName
        |> Result.bind (fun (_, meta) ->
            // Same steering the Zarr provider applies: StaticValue has no
            // packed carrier, so a pool would fold to a WRONG dense shape.
            if meta.Blade.IsSome then
                Error $"variable '{varName}' has a packed (blade: layout=packed) pool layout -- triangular and orbit (iterated-wreath) variables do not fold at compile time; bind with a plain `let ... |> <alias>.read`"
            else Error pendingChunkSource)

/// Wreath (OrbIdx depth >= 2) canonical pool read. Presence of this function
/// in the spec is the provider's wreath CAPABILITY flag at every seam; the
/// pool itself rides the shared chunk-source core.
let readWreathPool (path: string) (varName: string) : Result<Blade.ProviderRegistry.ProviderVarData, string> =
    match load path with
    | Error e -> Error e
    | Ok (LoadedRepo _) ->
        Error $"'{path}' is an icechunk repo handle, not a checkout -- check a ref out first"
    | Ok (LoadedCheckout ck) ->
        findArray ck varName
        |> Result.bind (fun (_, meta) ->
            match meta.Blade with
            | Some l when l.Group.Sym = Blade.Types.SymWreath -> Error pendingChunkSource
            | Some _ -> Error $"variable '{varName}' has a depth-1 packed (sym/antisym) layout, not an orbit head"
            | None -> Error $"variable '{varName}' is an ordinary dense array, not an orbit (iterated-wreath) pool")

// ---------------------------------------------------------------------------
// Mapping to Blade IR modules
// ---------------------------------------------------------------------------

/// The repo-handle module: NO dims and NO vars structs. This is a first-class
/// outcome of `registerProviderModule` (its `moduleFields` simply come out
/// []), and it is what makes `ic.load(path)` a handle rather than a store:
/// the binding carries no fields, so no alias verb can mis-fire against it,
/// and `checkout` is the only way to reach data.
let emptyRepoModule (moduleName: string) : IRModule = {
    Name = moduleName
    Types = []
    Functions = []
    Bindings = []
    StaticFunctionUsage = Map.empty
    ProviderReads = Map.empty
    ProviderWrites = Map.empty
    RandomInits = Map.empty
    CompoundInits = Map.empty
    SparseInits = Map.empty
    MutableArrayLets = Set.empty
    DerivedFuncOrigins = Map.empty
}

/// The dims/vars module for a resolved checkout. Node user data is verbatim
/// `zarr.json`, so this delegates to the Zarr provider's `zarrStoreToModule`
/// once `decodePayload` yields NodeMeta values -- passing `externalDimMap`
/// from P3's axis-mint table, which is how two checkouts of one repo come to
/// share index-type identity for an unchanged axis (§5.3).
///
/// Unreachable today: `load` stops at the payload seam above it.
let checkoutToModule (builder: IRBuilder) (moduleName: string) (ck: CheckoutHandle) : IRModule =
    ignore builder
    ignore moduleName
    let (kind, name) = ck.Ref
    failwith $"icechunk checkout ({kindToken kind}:{name}) of '{ck.Repo.Root}': {pendingPayloadDecode}"

/// Provider contract entry point. A BARE path binds the repo handle (an empty
/// module) after the header-level validation that can run today; a canonical
/// key with a refspec builds the full dims/vars module.
let loadAsModule (builder: IRBuilder) (moduleName: string) (path: string) : IRModule =
    match parseKey path with
    | Error e -> failwith e
    | Ok key ->
        match key.Ref with
        | None ->
            match validateRepoFile key.RepoPath with
            | Error e -> failwith e
            | Ok _ -> emptyRepoModule moduleName
        | Some _ ->
            match load path with
            | Ok (LoadedCheckout ck) -> checkoutToModule builder moduleName ck
            | Ok (LoadedRepo _) ->
                failwith $"icechunk: internal -- canonical key '{path}' carries a refspec but resolved to a bare repo handle"
            | Error e -> failwith $"icechunk checkout '{path}': {e}"

// ---------------------------------------------------------------------------
// Fingerprint / version stamp
// ---------------------------------------------------------------------------

/// Fold-memoization stamp: mtime ticks of `$ROOT/repo`. REAL today, and
/// exact: the repo file is the ONLY mutable object in a repo, so one O(1)
/// stat replaces the Zarr provider's max-mtime walk over every file. (Polish,
/// not v1: `tag:` and `snapshot:` keys are immutable and could skip
/// invalidation entirely.)
let versionStamp (path: string) : int64 =
    try
        match parseKey path with
        | Ok key ->
            let file = repoFilePath key.RepoPath
            if File.Exists file then File.GetLastWriteTimeUtc(file).Ticks else 0L
        | Error _ -> 0L
    with _ -> 0L

/// Fold-provenance token. §8 makes this the RESOLVED SNAPSHOT ID once the
/// payload decoder lands -- the semantically right provenance, with no
/// sha256 sweep over the store. Until then it is a stable PLACEHOLDER:
/// sha256 over the canonical key plus the mutable repo file's bytes, so the
/// same key against the same repo state yields the same token, and a commit
/// (which rewrites the repo file) changes it. Never throws.
let fingerprint (path: string) : string =
    use sha = System.Security.Cryptography.SHA256.Create()
    let keyBytes = Text.Encoding.UTF8.GetBytes(path + "\n")
    sha.TransformBlock(keyBytes, 0, keyBytes.Length, null, 0) |> ignore
    let repoBytes =
        try
            match parseKey path with
            | Ok key ->
                let file = repoFilePath key.RepoPath
                if File.Exists file then File.ReadAllBytes file else [||]
            | Error _ -> [||]
        with _ -> [||]
    if repoBytes.Length > 0 then
        sha.TransformBlock(repoBytes, 0, repoBytes.Length, null, 0) |> ignore
    sha.TransformFinalBlock([||], 0, 0) |> ignore
    sha.Hash |> Array.map (sprintf "%02x") |> String.concat ""

// ---------------------------------------------------------------------------
// C++ code generation (pure std C++17 -- no Icechunk logic in the binary)
// ---------------------------------------------------------------------------

module CppIcechunk =

    /// Required includes. The emitted reader opens files by baked path and
    /// reads baked byte ranges -- the same std-only surface the Zarr provider
    /// needs, which is what keeps LinkNeeds = "none".
    let genIncludes () : string list =
        [ "#include <fstream>"
          "#include <filesystem>"
          "#include <cstdint>"
          "#include <string>"
          "#include <limits>" ]

    /// Why a read cannot be emitted yet. Pre-payload refusals (bad path,
    /// missing repo, spec-1 header, unknown compression, malformed key) are
    /// preferred over the seam's message, so a user who mistyped a path is
    /// told about the path, not about a deferred dependency decision.
    let private readBlocker (path: string) : string =
        match load path with
        | Ok _ -> pendingChunkSource
        | Error e -> e

    /// Dense reader. Emits a compile-time-baked chunk table (relative path,
    /// offset, length per grid coordinate; inline chunks as byte-array
    /// literals; a sentinel for fill) plus one open/seek/read/copy loop --
    /// see plan §7. Refuses loudly until the seam lands.
    let genReadVar (path: string) (varName: string) (cppVarName: string) (arrType: IRArrayType) : string list =
        ignore cppVarName
        ignore arrType
        failwith $"icechunk read of variable '{varName}' from '{path}': {readBlocker path}"

    /// Packed (SymIdx/AntisymIdx) reader; rides the same baked table through
    /// the shared chunk-source core, including the windowed and
    /// MPI-distributed forms.
    let genReadPacked (path: string) (varName: string) (cppVarName: string) (arrType: IRArrayType) (opts: Blade.ProviderRegistry.PackedReadOpts) : string list =
        ignore cppVarName
        ignore arrType
        ignore opts
        failwith $"icechunk packed read of variable '{varName}' from '{path}': {readBlocker path}"

    /// Writes are refused BY NAME: an Icechunk write is a COMMIT, not an
    /// in-place store write (§8, §11).
    let genWriteVar (path: string) (varName: string) (cppVarName: string) (arrType: IRArrayType) (dimNames: string list) : string list =
        ignore cppVarName
        ignore arrType
        ignore dimNames
        failwith $"icechunk write of '{varName}' to '{path}' is refused: writing to an Icechunk repo is a COMMIT -- new chunk files, a new manifest, a new snapshot, and an optimistic-concurrency conditional swap of the mutable 'repo' file -- not an in-place store write. Writes-as-commits are their own arc (docs/plans/plan-icechunk-provider.md §11); write a plain Zarr store instead and ingest it with icechunk-python"

// ---------------------------------------------------------------------------
// Provider registration record (plan §8)
// ---------------------------------------------------------------------------

/// The icechunk ProviderSpec (surface module name "icechunk").
let spec : Blade.ProviderRegistry.ProviderSpec = {
    Name = "icechunk"
    LoadAsModule = loadAsModule
    ReadVarData = readVarData
    GenReadVar = CppIcechunk.genReadVar
    // Presence of these two is the provider's packed/wreath CAPABILITY
    // declaration at every codegen and interpreter seam (§8 lists both as v1
    // via the shared chunk-source core). They refuse with a NAMED pending
    // message rather than the generic "this provider has no packed support".
    GenReadPacked = Some CppIcechunk.genReadPacked
    ReadWreathPool = Some readWreathPool
    GenReadCompoundVar = None  // load_compound: refused loudly, as Zarr
    GenWriteVar = CppIcechunk.genWriteVar
    GenStreamOpen = None       // `.stream`: deferred (the baked table makes fiber reads easy later)
    GenStreamFiber = None
    Includes = CppIcechunk.genIncludes
    VarDimNames = fun path varName ->
        // Must not throw on an unreadable store: writers fall back to
        // synthesized dim<i> names when this yields None.
        try
            match load path with
            | Ok (LoadedCheckout ck) ->
                ck.Snapshot.Nodes
                |> List.tryFind (fun n -> n.Path = "/" + varName)
                |> Option.bind (fun n -> n.DimensionNames)
            | _ -> None
        with _ -> None
    Fingerprint = fingerprint
    VersionStamp = versionStamp
    LinkNeeds = "none (pure std C++17)"
}
