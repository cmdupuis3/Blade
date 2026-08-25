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
// THE PAYLOAD DECODE (plan §6.2, DECIDED: option B). Metadata payloads are
// (usually zstd-compressed) FlatBuffers. zstd is ZstdSharp.Port, a MANAGED-only
// port -- no native library, so the compiler keeps its "pure .NET at compile
// time" property; the FlatBuffers accessors are the flatc-generated C# vendored
// under providers/icechunk-format (namespace `generated`), pinned to icechunk
// v2.1.2's own schemas. Both are COMPILE-TIME-ONLY dependencies: neither
// reaches the emitted program, because every ref, manifest and chunk byte
// range is resolved here and baked as static tables (see CppIcechunk below).
//
// CONSISTENCY. Ref resolution (and everything under it) is memoized per
// (canonical key, repo-file mtime) -- plan §3.1 -- so typecheck, static folds,
// lowering and codegen all see the SAME snapshot even if a writer commits
// mid-compilation. That closes a TOCTOU the Zarr provider structurally has,
// since it re-walks its store directory in every phase.
module Blade.IcechunkProvider

open System
open System.Collections.Concurrent
open System.IO
open Blade.IR
open Blade.Types

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
    /// The schema's `parent_offset`: the parent's ABSOLUTE index in this same
    /// list ("offset from the start of the list, not from this entry"), with
    /// -1 meaning no parent -- the initial snapshot. The READ path never walks
    /// ancestry (§5.2 rejects tx-log/ancestry mechanisms outright); this is
    /// carried for provenance only.
    ParentOffset: int
    /// Commit time, Unix MILLISECONDS. The wire field is microseconds
    /// (`flushed_at`, "non-leap microseconds since Jan 1, 1970 UTC"); the
    /// decoder divides.
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

/// One dimension as the SNAPSHOT records it structurally (ArrayNodeData's
/// `shape_v2`, or the V1 `shape` when a repo still carries one). Cross-checked
/// against the node's own `zarr.json` -- the two must agree (§6.1).
type DimShape = {
    /// Total elements along this dimension.
    ArrayLength: int64
    /// Chunks along this dimension. `None` when the record came from the V1
    /// `DimensionShape`, whose `chunk_length` the schema itself marks
    /// possibly-inaccurate ("the authoritative chunk size comes from the Zarr
    /// metadata in user_data"), so there is nothing trustworthy to compare.
    NumChunks: int64 option
}

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
    /// Structural shape from ArrayNodeData; empty for a group.
    Shape: DimShape list
    /// Dimension names as stored STRUCTURALLY in ArrayNodeData -- cross-checked
    /// against the JSON's `dimension_names` (loud on disagreement, §6.1).
    /// None when the snapshot leaves any of them unnamed.
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

/// A native chunk ref: a byte range of the file `$ROOT/chunks/<ChunkId>`. The
/// reference writer emits one chunk per file (offset 0), but the schema
/// permits packing, so readers must honor offset and length.
///
/// The 12-byte id rather than a path, so the decoders stay PURE -- a decoded
/// manifest says the same thing wherever the repo happens to sit, and unit
/// tests need no repo on disk. `nativeChunkFile` joins it to a root.
and NativeChunk = {
    ChunkId: byte[]
    Offset: int64
    Length: int64
}

/// The file a native chunk ref names, under a repo root.
let nativeChunkFile (root: string) (nc: NativeChunk) : string =
    chunkPath root nc.ChunkId

/// The named refusal for a virtual chunk ref (§9). The decoder calls this
/// rather than inventing a ChunkLoc case, so the refusal has one wording.
/// At decode time the owner is known only by its (spec-stable) NODE ID, which
/// is what `arrayName` carries.
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

/// A chunk-grid coordinate, for diagnostics: "[0, 3]".
let private coordText (c: int64 list) : string =
    "[" + (c |> List.map string |> String.concat ", ") + "]"

// ---------------------------------------------------------------------------
// Payload decode: zstd (ZstdSharp.Port) + FlatBuffers (vendored accessors)
// ---------------------------------------------------------------------------

/// A decode refusal whose message is ALREADY FINAL. It travels out of the
/// decoders unwrapped, so a named refusal (a virtual chunk ref, an unknown
/// union arm, a violated exactly-one rule) is never buried under a generic
/// "malformed FlatBuffer".
exception IcechunkDecodeError of string

let private iceErr (message: string) = raise (IcechunkDecodeError message)

/// Ceiling on a decompressed metadata payload. Metadata is small even for big
/// stores (a manifest for a million chunks is tens of MB); the cap exists so a
/// corrupt frame header cannot ask for the address space.
let maxPayloadBytes = 1 <<< 30

// zstd's two sentinel returns from a frame header: the frame recorded no
// content size, or the header could not be read at all.
let private zstdContentSizeUnknown = UInt64.MaxValue - 1UL
let private zstdContentSizeError = UInt64.MaxValue

/// zstd decompression of a metadata payload (header compression byte 1),
/// through the managed ZstdSharp port. Sized from the frame's content size
/// when it records one, otherwise by grow-and-retry -- icechunk's writer uses
/// the streaming encoder, which does not always pledge a size. Every failure
/// names the BYTE COUNTS: "the payload did not decompress" without them is
/// unactionable.
let decompress (bytes: byte[]) : Result<byte[], string> =
    if isNull (box bytes) then
        Error "icechunk: zstd decompression was handed a null payload"
    elif bytes.Length = 0 then
        Error $"icechunk: the file carries a {headerSize}-byte header and NO payload -- a zstd frame is never zero bytes"
    else
    try
        use dec = new ZstdSharp.Decompressor()
        let hinted = ZstdSharp.Decompressor.GetDecompressedSize(bytes, 0, bytes.Length)
        let sized = hinted <> zstdContentSizeUnknown && hinted <> zstdContentSizeError
        if sized && hinted = 0UL then Ok [||]
        elif sized && hinted > uint64 maxPayloadBytes then
            Error $"icechunk: the zstd frame in a {bytes.Length}-byte payload declares {hinted} decompressed bytes, past this reader's {maxPayloadBytes}-byte metadata cap"
        elif sized then
            let out = Array.zeroCreate<byte> (int hinted)
            let written = dec.Unwrap(bytes, 0, bytes.Length, out, 0, out.Length)
            if written <> out.Length then
                Error $"icechunk: zstd decompression of a {bytes.Length}-byte payload produced {written} bytes, but the frame header declares {out.Length}"
            else Ok out
        else
            // No content size in the frame header: grow and retry.
            let rec grow (cap: int) : Result<byte[], string> =
                let out = Array.zeroCreate<byte> cap
                let mutable written = 0
                if dec.TryUnwrap(bytes, 0, bytes.Length, out, 0, cap, &written) then
                    Ok (if written = cap then out else Array.sub out 0 written)
                elif cap >= maxPayloadBytes then
                    Error $"icechunk: the zstd frame in a {bytes.Length}-byte payload does not record its decompressed size and needs more than this reader's {maxPayloadBytes}-byte metadata cap"
                else
                    grow (min maxPayloadBytes (cap * 2))
            let start = int (min (int64 maxPayloadBytes) (max 65536L (int64 bytes.Length * 8L)))
            grow start
    with ex ->
        Error $"icechunk: zstd decompression of a {bytes.Length}-byte payload failed: {ex.Message}"

/// Post-header bytes -> plaintext payload bytes, per the header's compression
/// byte. Identity for compression 0; `decompress` for compression 1.
let decompressPayload (compression: Compression) (bytes: byte[]) : Result<byte[], string> =
    match compression with
    | CompNone -> Ok bytes
    | CompZstd -> decompress bytes

/// The 12 bytes of a required `ObjectId12` (snapshot / manifest / chunk id).
let private oid12 (id: Nullable<generated.ObjectId12>) (what: string) : byte[] =
    if not id.HasValue then
        iceErr $"icechunk: {what} is missing its 12-byte object id (a required FlatBuffers field)"
    else
        let v = id.Value
        Array.init 12 (fun j -> v.Bytes j)

/// The 8 bytes of a required `ObjectId8` (node id).
let private oid8 (id: Nullable<generated.ObjectId8>) (what: string) : byte[] =
    if not id.HasValue then
        iceErr $"icechunk: {what} is missing its 8-byte node id (a required FlatBuffers field)"
    else
        let v = id.Value
        Array.init 8 (fun j -> v.Bytes j)

/// `Repo` root table -> RepoInfo. Branches and tags stay SEPARATE lists (they
/// are separate namespaces), deleted tags come across as tombstones, and the
/// availability enum is mapped by name rather than by a numeric fallthrough.
let private decodeRepoInfo (bytes: byte[]) : RepoInfo =
    let r = generated.Repo.GetRootAsRepo(Google.FlatBuffers.ByteBuffer(bytes))
    // The table repeats the header's spec byte. 0 means the field was left at
    // its flatbuffers default (absent); a genuine disagreement means the repo
    // file contradicts its own header.
    if r.SpecVersion <> 0uy && int r.SpecVersion <> supportedSpecVersion then
        iceErr $"icechunk repo file: the Repo table records spec version {int r.SpecVersion}, but the file header records {supportedSpecVersion} -- the repo file contradicts itself"
    if not r.Status.HasValue then
        iceErr "icechunk repo file: no RepoStatus table (a required field) -- availability is unknown, and this reader will not guess Online"
    let status =
        match r.Status.Value.Availability with
        | generated.RepoAvailability.Online -> StatusOnline
        | generated.RepoAvailability.ReadOnly -> StatusReadOnly
        | generated.RepoAvailability.Offline -> StatusOffline
        | other ->
            iceErr $"icechunk repo file: unknown RepoAvailability {int other} -- this reader knows Online (0), ReadOnly (1) and Offline (2)"
    let refList (len: int) (get: int -> Nullable<generated.Ref>) (what: string) =
        [ for i in 0 .. len - 1 ->
            let rf = get i
            if not rf.HasValue then iceErr $"icechunk repo file: {what} entry {i} is absent"
            elif isNull rf.Value.Name then iceErr $"icechunk repo file: {what} entry {i} has no name (a required field)"
            else (rf.Value.Name, int rf.Value.SnapshotIndex) ]
    { Branches = refList r.BranchesLength (fun i -> r.Branches(i)) "branches"
      Tags = refList r.TagsLength (fun i -> r.Tags(i)) "tags"
      DeletedTags =
        [ for i in 0 .. r.DeletedTagsLength - 1 ->
            let t = r.DeletedTags(i)
            if isNull t then iceErr $"icechunk repo file: deleted_tags entry {i} is null" else t ]
      Snapshots =
        [ for i in 0 .. r.SnapshotsLength - 1 ->
            let s = r.Snapshots(i)
            if not s.HasValue then iceErr $"icechunk repo file: snapshots entry {i} is absent"
            else
                let sv = s.Value
                { Id = oid12 sv.Id $"repo file snapshot entry {i}"
                  ParentOffset = sv.ParentOffset
                  FlushedAtMillis = int64 (sv.FlushedAt / 1000UL)
                  Message = (if isNull sv.Message then "" else sv.Message) } ]
      Status = status }

/// `Snapshot` root table -> Snapshot. `user_data` is decoded as UTF-8 and kept
/// VERBATIM (it is the node's zarr.json); the structural shape and dimension
/// names come across so `arrayMetaOfNode` can cross-check them against it.
let private decodeSnapshot (bytes: byte[]) : Snapshot =
    let s = generated.Snapshot.GetRootAsSnapshot(Google.FlatBuffers.ByteBuffer(bytes))
    let nodes =
        [ for i in 0 .. s.NodesLength - 1 ->
            let nOpt = s.Nodes(i)
            if not nOpt.HasValue then iceErr $"icechunk snapshot: node entry {i} is absent"
            else
            let ns = nOpt.Value
            let path =
                if isNull ns.Path then iceErr $"icechunk snapshot: node entry {i} has no path (a required field)"
                else ns.Path
            let ud = ns.GetUserDataArray()
            if isNull ud then
                iceErr $"icechunk snapshot node '{path}': no user_data (a required field) -- user_data IS the node's zarr.json, so there is no metadata to read"
            let json = Text.Encoding.UTF8.GetString ud
            let nodeId = oid8 ns.Id $"snapshot node '{path}'"
            match ns.NodeDataType with
            | generated.NodeData.Group ->
                { Id = nodeId; Path = path; Kind = NodeGroup; UserDataJson = json
                  Shape = []; DimensionNames = None; ManifestRefs = [] }
            | generated.NodeData.Array ->
                let a = ns.NodeDataAsArray()
                let shape =
                    if a.ShapeV2Length > 0 then
                        [ for d in 0 .. a.ShapeV2Length - 1 ->
                            let ds = a.ShapeV2(d)
                            if not ds.HasValue then iceErr $"icechunk snapshot node '{path}': shape_v2 entry {d} is absent"
                            else { ArrayLength = int64 ds.Value.ArrayLength; NumChunks = Some (int64 ds.Value.NumChunks) } ]
                    elif a.ShapeLength > 0 then
                        [ for d in 0 .. a.ShapeLength - 1 ->
                            let ds = a.Shape(d)
                            if not ds.HasValue then iceErr $"icechunk snapshot node '{path}': shape entry {d} is absent"
                            else { ArrayLength = int64 ds.Value.ArrayLength; NumChunks = None } ]
                    else []
                let dimNames =
                    if a.DimensionNamesLength = 0 then None
                    else
                        let ns_ =
                            [ for d in 0 .. a.DimensionNamesLength - 1 ->
                                let dn = a.DimensionNames(d)
                                if dn.HasValue && not (isNull dn.Value.Name) then dn.Value.Name else "" ]
                        if ns_ |> List.exists String.IsNullOrEmpty then None else Some ns_
                let manifests =
                    [ for m in 0 .. a.ManifestsLength - 1 ->
                        let mrOpt = a.Manifests(m)
                        if not mrOpt.HasValue then iceErr $"icechunk snapshot node '{path}': manifest ref {m} is absent"
                        else
                            let mr = mrOpt.Value
                            let mid = oid12 mr.ObjectId $"manifest ref {m} of snapshot node '{path}'"
                            let extents =
                                [ for e in 0 .. mr.ExtentsLength - 1 ->
                                    let rg = mr.Extents(e)
                                    if not rg.HasValue then iceErr $"icechunk snapshot node '{path}': manifest ref {m} has no extent for dimension {e}"
                                    else (int64 rg.Value.From, int64 rg.Value.To) ]
                            (mid, extents) ]
                { Id = nodeId; Path = path; Kind = NodeArray; UserDataJson = json
                  Shape = shape; DimensionNames = dimNames; ManifestRefs = manifests }
            | other ->
                iceErr $"icechunk snapshot node '{path}': the NodeData union arm is {int other}, which is neither Array (1) nor Group (2) -- this snapshot was written by a format this reader does not know" ]
    { Id = oid12 s.Id "snapshot"; Nodes = nodes }

/// One `ChunkRef` -> ChunkLoc, enforcing the schema's EXACTLY-ONE rule across
/// the three ref forms. Presence is read from the FlatBuffers field offsets
/// (an absent vector is null, an absent struct has no value), never from a
/// length -- an empty inline vector is "present and empty", which is a
/// different fact from "not inline".
let private decodeChunkRef (owner: string) (cr: generated.ChunkRef) : ChunkRef =
    let index = [ for j in 0 .. cr.IndexLength - 1 -> int64 (cr.Index(j)) ]
    let inlineBytes = cr.GetInlineArray()
    let hasInline = not (isNull inlineBytes)
    let chunkId = cr.ChunkId
    let hasNative = chunkId.HasValue
    let location = cr.Location
    let hasLocation = not (isNull location)
    let hasCompressedLocation = not (isNull (cr.GetCompressedLocationArray()))
    let hasVirtual = hasLocation || hasCompressedLocation
    let forms = (if hasInline then 1 else 0) + (if hasNative then 1 else 0) + (if hasVirtual then 1 else 0)
    if forms <> 1 then
        let named =
            [ (if hasInline then Some "inline" else None)
              (if hasNative then Some "chunk_id" else None)
              (if hasLocation then Some "location" else None)
              (if hasCompressedLocation then Some "compressed_location" else None) ]
            |> List.choose id
        let listing = if List.isEmpty named then "none of them" else String.concat " AND " named
        iceErr $"icechunk manifest for node '{owner}': chunk ref {coordText index} sets {listing} -- the schema requires EXACTLY ONE of inline / chunk_id / location to be present. (A writer using the flatbuffers OBJECT api must null the unused fields explicitly: ChunkRefT initializes chunk_id to a zero object id, and that zero id lands on the wire.)"
    elif hasVirtual then
        iceErr (virtualChunkRefused owner (if hasLocation then location else "<zstd-dictionary-compressed location>"))
    elif hasInline then
        { Index = index; Loc = Inline inlineBytes }
    else
        let cid = oid12 chunkId $"native chunk ref {coordText index} of node '{owner}'"
        { Index = index
          Loc = Native { ChunkId = cid; Offset = int64 cr.Offset; Length = int64 cr.Length } }

/// `Manifest` root table -> the per-array chunk tables it holds.
let private decodeManifest (bytes: byte[]) : ArrayManifest list =
    let m = generated.Manifest.GetRootAsManifest(Google.FlatBuffers.ByteBuffer(bytes))
    [ for i in 0 .. m.ArraysLength - 1 ->
        let amOpt = m.Arrays(i)
        if not amOpt.HasValue then iceErr $"icechunk manifest: array entry {i} is absent"
        else
        let am = amOpt.Value
        let nodeId = oid8 am.NodeId $"manifest array entry {i}"
        let owner = base32Encode nodeId
        { NodeId = nodeId
          Refs =
            [ for j in 0 .. am.RefsLength - 1 ->
                let crOpt = am.Refs(j)
                if not crOpt.HasValue then iceErr $"icechunk manifest for node '{owner}': chunk ref entry {j} is absent"
                else decodeChunkRef owner crOpt.Value ] } ]

/// Post-header, post-decompression bytes -> domain values. The bytes are a
/// FlatBuffer whose root table is chosen by the file type. PURE: no repo path
/// enters here, so a decoded value says the same thing wherever the repo sits.
/// Transaction logs decode to nothing on purpose: they are PRUNABLE under 2.1,
/// so no read path may depend on their contents.
let decodePayload (fileType: FileType) (bytes: byte[]) : Result<Payload, string> =
    if isNull (box bytes) then
        Error $"icechunk: a null {fileTypeName fileType} payload"
    else
    try
        match fileType with
        | FtRepoInfo -> Ok (PRepoInfo (decodeRepoInfo bytes))
        | FtSnapshot -> Ok (PSnapshot (decodeSnapshot bytes))
        | FtManifest -> Ok (PManifest (decodeManifest bytes))
        | FtTransactionLog -> Ok PTransactionLog
        | FtAttributes | FtChunk | FtUnknown _ ->
            Error $"icechunk: {fileTypeName fileType} payloads have no reader -- this reader decodes RepoInfo, Snapshot and Manifest files only"
    with
    | IcechunkDecodeError m -> Error m
    | ex ->
        Error $"icechunk: the {fileTypeName fileType} payload ({bytes.Length} bytes) is not a readable FlatBuffer: {ex.Message}"

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
// Memoized resolution (plan §3.1)
// ---------------------------------------------------------------------------

/// mtime ticks of `$ROOT/repo`. The repo file is the ONLY mutable object in a
/// repo, so this one O(1) stat is a complete change stamp for the whole store
/// -- which is what makes it a sound memo key, and what `VersionStamp` reports.
let private repoStamp (repoPath: string) : int64 =
    try
        let f = repoFilePath repoPath
        if File.Exists f then File.GetLastWriteTimeUtc(f).Ticks else 0L
    with _ -> 0L

/// The memo key's stamp: mtime ticks AND byte length. The length is there for
/// the fixture case -- a generated repo REWRITTEN AT THE SAME PATH within one
/// filesystem timestamp tick would otherwise hit a stale entry. Test code that
/// regenerates a repo in place should still call `resetCaches` rather than
/// rely on this; `VersionStamp` deliberately stays plain mtime ticks (§8).
let private repoMemoStamp (repoPath: string) : int64 * int64 =
    try
        let f = repoFilePath repoPath
        if File.Exists f then
            let fi = FileInfo f
            (fi.LastWriteTimeUtc.Ticks, fi.Length)
        else (0L, 0L)
    with _ -> (0L, 0L)

/// Bound on each memo, so a long-lived process (the REPL, the test harness)
/// cannot grow one without limit. Correctness never depends on a hit: a miss
/// just re-reads the same immutable files.
let private memoCap = 512

let private memoize (d: ConcurrentDictionary<'k, 'v>) (k: 'k) (f: unit -> 'v) : 'v =
    if d.Count > memoCap then d.Clear()
    d.GetOrAdd(k, Func<'k, 'v>(fun _ -> f ()))

/// Registered clearers, so `resetCaches` does not have to name every memo (and
/// cannot silently miss one added later).
let private memoClearers = ResizeArray<unit -> unit>()

let private newMemo<'k, 'v when 'k: equality> () : ConcurrentDictionary<'k, 'v> =
    let d = ConcurrentDictionary<'k, 'v>()
    memoClearers.Add(fun () -> d.Clear())
    d

/// Drop every memoized read. Compilation never needs this -- the stamps handle
/// it -- but a test that regenerates a fixture repo AT THE SAME PATH does.
let resetCaches () : unit =
    for clear in memoClearers do clear ()

// ---------------------------------------------------------------------------
// Reading metadata files
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
/// from 39 bytes: magic, spec version, file type, compression byte.
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
/// header with a known compression byte. This is what a bare `ic.load(path)`
/// runs before it binds the repo handle.
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

/// Read a metadata file whole: header, file-type check, decompression, and the
/// plaintext payload bytes out.
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

/// Read and decode `$ROOT/repo`.
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

let private snapshotMemo = newMemo<string * string * (int64 * int64), Result<Snapshot, string>> ()
let private manifestMemo = newMemo<string * string * (int64 * int64), Result<ArrayManifest list, string>> ()

/// Read and decode `$ROOT/snapshots/<id>`. Snapshots are immutable, so the
/// memo only ever re-reads after the repo file itself changed.
let readSnapshot (root: string) (snapshotId: byte[]) : Result<Snapshot, string> =
    memoize snapshotMemo (root, base32Encode snapshotId, repoMemoStamp root) (fun () ->
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
                    | other -> Error $"{where_} decoded as a {payloadKindName other} table, not a Snapshot")))

/// Read and decode `$ROOT/manifests/<id>`. One manifest commonly covers
/// several arrays, so this memo is what keeps a multi-variable program from
/// re-decompressing the same file per variable.
let readManifest (root: string) (manifestId: byte[]) : Result<ArrayManifest list, string> =
    memoize manifestMemo (root, base32Encode manifestId, repoMemoStamp root) (fun () ->
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
                    | other -> Error $"{where_} decoded as a {payloadKindName other} table, not a Manifest")))

let private loadMemo = newMemo<string * (int64 * int64), Result<Loaded, string>> ()

let private loadUncached (key: RepoKey) : Result<Loaded, string> =
    readRepoHandle key.RepoPath
    |> Result.bind (fun repo ->
        match key.Ref with
        // A bare handle carries no ref to resolve, so `resolveRef`'s own
        // status gate never runs for it -- gate here instead, so an Offline
        // repo refuses at ic.load itself rather than only at first checkout.
        | None -> statusGate repo.Info |> Result.map (fun () -> LoadedRepo repo)
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
                        Snapshot = snap })))

/// Open a canonical key: parse it, read the repo file, and (when the key
/// carries a refspec) resolve the ref and read its snapshot.
///
/// MEMOIZED per (key, repo-file mtime), which is the plan's §3.1 consistency
/// point: typecheck, static folds, lowering and codegen all resolve through
/// here, and while the repo file is unchanged they all get the SAME snapshot,
/// even if a writer commits mid-compilation.
let load (path: string) : Result<Loaded, string> =
    match parseKey path with
    | Error e -> Error e
    | Ok key -> memoize loadMemo (path, repoMemoStamp key.RepoPath) (fun () -> loadUncached key)

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

/// Parse one node's `zarr.json` user data with the ZARR provider's v3 parser
/// -- the JSON is verbatim `zarr.json` (§2), so every Zarr v1 gate applies
/// unchanged -- and then CROSS-CHECK the snapshot's own structural record
/// against it (§6.1): rank, per-dimension extent, chunk count, dimension
/// names. A snapshot that disagrees with the metadata it carries is a
/// corrupt repo, and this is the only place that can notice.
///
/// `arrayDir` is empty on purpose: chunks live under $ROOT/chunks by object
/// id, never beside the metadata, and this provider never takes the chunk-key
/// path, so there is no directory to name.
let private arrayMetaOfNode (varName: string) (node: NodeMeta) : Result<ZarrProvider.ZarrArrayMeta, string> =
    ZarrProvider.parseArrayMetaV3 varName "" node.UserDataJson
    |> Result.mapError (fun e -> $"icechunk array '{node.Path}': {e}")
    |> Result.bind (fun meta ->
        let where_ = $"icechunk array '{node.Path}'"
        if node.Shape.Length <> meta.Shape.Length then
            Error $"{where_}: the snapshot records {node.Shape.Length} structural dimension(s), but the zarr.json it carries declares rank {meta.Shape.Length} -- the snapshot disagrees with its own metadata"
        else
            let grid = ZarrProvider.gridDims meta.Shape meta.Chunks
            let dimErr =
                List.zip node.Shape (List.zip meta.Shape grid)
                |> List.mapi (fun i (ds, (ext, nch)) ->
                    if ds.ArrayLength <> ext then
                        Some $"{where_}: the snapshot records extent {ds.ArrayLength} for dimension {i}, but its zarr.json declares {ext}"
                    else
                        match ds.NumChunks with
                        | Some k when k <> nch ->
                            Some $"{where_}: the snapshot records {k} chunk(s) along dimension {i}, but its zarr.json's shape/chunk_shape gives {nch}"
                        | _ -> None)
                |> List.tryPick id
            match dimErr with
            | Some e -> Error e
            | None ->
                match node.DimensionNames, meta.DimNames with
                | Some sn, Some jn when sn <> jn ->
                    Error $"""{where_}: the snapshot names its dimensions {String.concat ", " sn}, but its zarr.json names them {String.concat ", " jn}"""
                | Some sn, None ->
                    Error $"""{where_}: the snapshot names its dimensions {String.concat ", " sn}, but the zarr.json it carries has no dimension_names -- the module would be built over synthesized axes while the snapshot names real ones"""
                | _ -> Ok meta)

/// Find a variable in a checkout and parse (and cross-check) its metadata. A
/// name that exists only inside a nested group is refused BY NAME rather than
/// reported as missing.
let findArray (ck: CheckoutHandle) (varName: string) : Result<NodeMeta * ZarrProvider.ZarrArrayMeta, string> =
    let wanted = "/" + varName
    match ck.Snapshot.Nodes |> List.tryFind (fun n -> n.Path = wanted && n.Kind = NodeArray) with
    | Some node ->
        arrayMetaOfNode varName node |> Result.map (fun meta -> (node, meta))
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
// The resolved array: metadata + baked chunk table
// ---------------------------------------------------------------------------

/// One array of one checkout, fully resolved at compile time: its metadata
/// plus the chunk table (one `ChunkLoc` per chunk-grid coordinate, row-major).
/// The F# fold path and the C++ emitter read EXACTLY this, so they cannot
/// disagree about where a chunk lives.
type ResolvedArray = {
    /// The repo path AS GIVEN -- a relative path stays relative, which is what
    /// makes a baked path resolve against the emitted program's working
    /// directory (netcdf/zarr parity), not the compiler's.
    Root: string
    Ref: RefKind * string
    SnapshotId: byte[]
    VarName: string
    Node: NodeMeta
    Meta: ZarrProvider.ZarrArrayMeta
    /// Row-major over the chunk grid; `Fill` where no manifest covers a coordinate.
    Table: ChunkLoc[]
}

let private repoHandleRefusal (path: string) : string =
    $"'{path}' is an icechunk REPO HANDLE, not a checkout -- a repo handle has no variables; check a ref out first (`repo.checkout(\"main\")`, whose canonical key is '{path}@branch:main')"

/// Read every manifest an array's node points at, taking only ITS chunk table
/// out of each. Loud when a manifest ref's extents are the wrong rank, when a
/// named manifest holds no table for this node, or when a manifest holds a
/// chunk outside the extents it declares (§2: the extents ARE the coverage
/// claim the non-overlap invariant is built on).
let private collectManifests (root: string) (node: NodeMeta) (meta: ZarrProvider.ZarrArrayMeta) : Result<ArrayManifest list, string> =
    let rank = meta.Shape.Length
    let rec go acc refs =
        match refs with
        | [] -> Ok (List.rev acc)
        | ((mid: byte[]), (extents: (int64 * int64) list)) :: rest ->
            let mname = base32Encode mid
            if extents.Length <> rank then
                Error $"icechunk array '{node.Path}': manifest '{mname}' declares {extents.Length} chunk-index range(s) for a rank-{rank} array"
            else
                match readManifest root mid with
                | Error e -> Error e
                | Ok arrays ->
                    match arrays |> List.tryFind (fun am -> am.NodeId = node.Id) with
                    | None ->
                        Error $"icechunk array '{node.Path}': manifest '{mname}' holds no chunk table for node {base32Encode node.Id} -- the snapshot points at a manifest that does not cover this array"
                    | Some am ->
                        let outside =
                            am.Refs |> List.tryFind (fun r ->
                                r.Index.Length <> rank
                                || List.exists2 (fun (c: int64) ((lo, hi): int64 * int64) -> c < lo || c >= hi) r.Index extents)
                        match outside with
                        | Some r ->
                            let ext = extents |> List.map (fun (a, b) -> $"[{a}, {b})") |> String.concat " x "
                            Error $"icechunk array '{node.Path}': manifest '{mname}' declares extents {ext} but holds chunk {coordText r.Index}, outside them"
                        | None -> go (am :: acc) rest
    go [] node.ManifestRefs

let private arrayMemo = newMemo<string * string * (int64 * int64), Result<ResolvedArray, string>> ()

/// Resolve one variable of one checkout all the way to its chunk table.
/// Memoized per (key, variable, repo-file mtime) alongside `load`, so the
/// typecheck, fold, lowering and codegen passes that each ask for the same
/// variable pay the manifest decode ONCE and see one answer.
let resolveArray (path: string) (varName: string) : Result<ResolvedArray, string> =
    match parseKey path with
    | Error e -> Error e
    | Ok key ->
        memoize arrayMemo (path, varName, repoMemoStamp key.RepoPath) (fun () ->
            match load path with
            | Error e -> Error e
            | Ok (LoadedRepo _) -> Error (repoHandleRefusal path)
            | Ok (LoadedCheckout ck) ->
                findArray ck varName
                |> Result.bind (fun (node, meta) ->
                    collectManifests ck.Repo.Root node meta
                    |> Result.bind (fun manifests ->
                        buildChunkTable meta manifests
                        |> Result.map (fun table ->
                            { Root = ck.Repo.Root
                              Ref = ck.Ref
                              SnapshotId = ck.SnapshotId
                              VarName = varName
                              Node = node
                              Meta = meta
                              Table = table }))))

// ---------------------------------------------------------------------------
// Compile-time payload read (the static fold), through the shared core
// ---------------------------------------------------------------------------

/// One native chunk's bytes: a byte range of an immutable file under
/// $ROOT/chunks. Failures throw, because `readArrayDataFrom` turns an
/// exception into an Error with this message -- never into silent zeros.
let private readNativeChunk (ra: ResolvedArray) (coords: int64 list) (nc: NativeChunk) : byte[] =
    let file = nativeChunkFile ra.Root nc
    if nc.Length < 0L || nc.Length > int64 maxPayloadBytes then
        failwith $"icechunk array '{ra.VarName}': chunk {coordText coords} declares a {nc.Length}-byte range, which is not a readable chunk length"
    if not (File.Exists file) then
        failwith $"icechunk array '{ra.VarName}': chunk file '{file}' for chunk {coordText coords} is missing -- chunk files are immutable, so a missing one means the snapshot was expired or garbage-collected"
    use fs = File.OpenRead file
    if nc.Offset < 0L || nc.Offset + nc.Length > fs.Length then
        failwith $"icechunk array '{ra.VarName}': chunk {coordText coords} claims bytes [{nc.Offset}, {nc.Offset + nc.Length}) of '{file}', which holds {fs.Length} bytes"
    fs.Seek(nc.Offset, SeekOrigin.Begin) |> ignore
    let buf = Array.zeroCreate<byte> (int nc.Length)
    let mutable off = 0
    let mutable n = 1
    while off < buf.Length && n > 0 do
        n <- fs.Read(buf, off, buf.Length - off)
        off <- off + n
    if off <> buf.Length then
        failwith $"icechunk array '{ra.VarName}': chunk {coordText coords} read {off} of {nc.Length} bytes from '{file}'"
    buf

/// The icechunk `ChunkSource` (plan §7): the baked table answers every
/// coordinate -- inline bytes come straight out of the manifest, a native
/// chunk is a byte range of a file under $ROOT/chunks, and a coordinate no
/// manifest covers is ABSENT, which the shared core turns into fill. Nothing
/// above this seam is icechunk-specific, so fill handling, edge intersection,
/// packed-pool reassembly and wreath pools cannot drift from Zarr's.
let private icechunkChunkSource (ra: ResolvedArray) : ZarrProvider.ChunkSource =
    let lens = ZarrProvider.gridDims ra.Meta.Shape ra.Meta.Chunks |> List.map int
    let strides = ZarrProvider.rowMajorStrides lens
    let flatOf (coords: int64 list) =
        List.fold2 (fun acc (c: int64) (s: int) -> acc + int c * s) 0 coords strides
    { Label = coordText
      Fetch = fun coords ->
        match ra.Table.[flatOf coords] with
        | Fill -> None
        | Inline bytes -> Some (ZarrProvider.decodeChunk ra.Meta.Codec bytes)
        | Native nc -> Some (ZarrProvider.decodeChunk ra.Meta.Codec (readNativeChunk ra coords nc)) }

let private adaptVarData (d: ZarrProvider.ZarrVarData) : Blade.ProviderRegistry.ProviderVarData =
    { DimLengths = d.DimLengths
      Payload =
        match d.Payload with
        | ZarrProvider.ZFloats xs -> Blade.ProviderRegistry.PFloats xs
        | ZarrProvider.ZInts xs -> Blade.ProviderRegistry.PInts xs }

/// Whole-variable read for `let static A = ic.read(ck.vars.A)`. Structured
/// through the real gates -- key parse, repo file, ref resolution, snapshot,
/// node lookup, zarr.json parse and cross-check, manifests, chunk table --
/// and then through the SHARED assembly core over the icechunk chunk source.
let readVarData (path: string) (varName: string) : Result<Blade.ProviderRegistry.ProviderVarData, string> =
    match load path with
    | Error e -> Error e
    | Ok (LoadedRepo _) -> Error (repoHandleRefusal path)
    | Ok (LoadedCheckout ck) ->
        findArray ck varName
        |> Result.bind (fun (_, meta) ->
            // Same steering the Zarr provider applies: StaticValue has no
            // packed carrier, so a pool would fold to a WRONG dense shape.
            if meta.Blade.IsSome then
                Error $"variable '{varName}' has a packed (blade: layout=packed) pool layout -- triangular and orbit (iterated-wreath) variables do not fold at compile time; bind with a plain `let ... |> <alias>.read`"
            else
                resolveArray path varName
                |> Result.bind (fun ra ->
                    ZarrProvider.readArrayDataFrom (icechunkChunkSource ra) ra.Meta
                    |> Result.map adaptVarData))

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
            | Some l when l.Group.Sym = SymWreath ->
                resolveArray path varName
                |> Result.bind (fun ra ->
                    ZarrProvider.readPackedPoolFrom (icechunkChunkSource ra) ra.Meta
                    |> Result.map adaptVarData)
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
/// `zarr.json`, so a checkout presents as a synthetic v3 ZarrStore and the
/// module itself is built by the Zarr provider's own `zarrStoreToModule` --
/// index-type minting, the dims/vars struct shapes, coordinate-array
/// detection and packed-layout typing are inherited, not re-derived.
///
/// `externalDimMap` is None: the axis mint table that lets two checkouts of
/// one repo SHARE an index type for an unchanged axis (§5.3) is phase P3.
///
/// Hierarchy (§9): root-level arrays only. Nested arrays are simply not
/// fields (the Zarr provider's one-level rule, where a subgroup directory is
/// never scanned); a root-level array whose name is not a Blade identifier
/// refuses loudly, because it would have to become a struct field.
let checkoutToModule (builder: IRBuilder) (moduleName: string) (ck: CheckoutHandle) : IRModule =
    let key = formatKey { RepoPath = ck.Repo.Root; Ref = Some ck.Ref }
    let arrays =
        ck.Snapshot.Nodes
        |> List.filter (fun n -> n.Kind = NodeArray)
        |> List.choose (fun n ->
            let rest = if n.Path.StartsWith "/" then n.Path.Substring 1 else n.Path
            if rest = "" || rest.Contains "/" then None
            else
                match nodeVarName n.Path with
                | Ok name -> Some (name, n)
                | Error e -> failwith e)
        |> List.sortBy fst
        |> List.map (fun (name, n) ->
            match arrayMetaOfNode name n with
            | Ok meta -> meta
            | Error e -> failwith e)
    let store : ZarrProvider.ZarrStore = { Path = key; Version = 3; Arrays = arrays }
    ZarrProvider.zarrStoreToModule builder moduleName store None

/// Provider contract entry point. A BARE path binds the repo handle (an empty
/// module) after header-level validation; a canonical key with a refspec
/// resolves the ref and builds the full dims/vars module.
let loadAsModule (builder: IRBuilder) (moduleName: string) (path: string) : IRModule =
    match parseKey path with
    | Error e -> failwith e
    | Ok key ->
        match key.Ref with
        | None ->
            // Route through `load`, not a bare `validateRepoFile`, so a
            // structurally fine but Offline repo still refuses here (plan
            // §3: the repo file is parsed, and its status gated, AT LOAD).
            match load path with
            | Error e -> failwith e
            | Ok (LoadedRepo _) -> emptyRepoModule moduleName
            | Ok (LoadedCheckout _) ->
                failwith $"icechunk: internal -- canonical key '{path}' carries no refspec but resolved to a checkout"
        | Some _ ->
            match load path with
            | Ok (LoadedCheckout ck) -> checkoutToModule builder moduleName ck
            | Ok (LoadedRepo _) ->
                failwith $"icechunk: internal -- canonical key '{path}' carries a refspec but resolved to a bare repo handle"
            | Error e -> failwith $"icechunk checkout '{path}': {e}"

// ---------------------------------------------------------------------------
// Fingerprint / version stamp
// ---------------------------------------------------------------------------

/// Fold-memoization stamp: mtime ticks of `$ROOT/repo`. Exact: the repo file
/// is the ONLY mutable object in a repo, so one O(1) stat replaces the Zarr
/// provider's max-mtime walk over every file. (Polish, not v1: `tag:` and
/// `snapshot:` keys are immutable and could skip invalidation entirely.)
let versionStamp (path: string) : int64 =
    try
        match parseKey path with
        | Ok key -> repoStamp key.RepoPath
        | Error _ -> 0L
    with _ -> 0L

/// The fallback provenance token for a path that names no snapshot: a bare
/// repo handle, a missing repo, an unresolvable ref. sha256 over the canonical
/// key plus the mutable repo file's bytes, so the same key against the same
/// repo state yields the same token. Never throws.
let private placeholderFingerprint (path: string) : string =
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

/// Fold-provenance token: the RESOLVED SNAPSHOT ID, prefixed with the refspec
/// that named it -- "branch:main@1CECHNKREP0F1RSTCMT0" (§8). This is the
/// semantically right provenance and it costs one already-memoized resolve,
/// with no sha256 sweep over the store: the snapshot is immutable, so the id
/// IS the content identity of everything the fold could have read. Never
/// throws; a path that names no snapshot falls back to the sha256 placeholder.
let fingerprint (path: string) : string =
    try
        match load path with
        | Ok (LoadedCheckout ck) ->
            let (kind, name) = ck.Ref
            $"{kindToken kind}:{name}@{base32Encode ck.SnapshotId}"
        | _ -> placeholderFingerprint path
    with _ -> placeholderFingerprint path

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

    let private elemCppOf (t: IRType) : string =
        match t with
        | IRTScalar ETFloat32 -> "float"
        | IRTScalar ETFloat64 -> "double"
        | IRTScalar ETInt32 -> "int"
        | IRTScalar ETInt64 -> "long long"
        | _ -> "double"

    let private normPath (p: string) : string = p.Replace('\\', '/')

    /// A C++ string literal for a baked path: separators normalized (the
    /// emitted program is not necessarily built on the compiling host), then
    /// escaped.
    let private cppPathLit (p: string) : string =
        "\"" + (normPath p).Replace("\\", "\\\\").Replace("\"", "\\\"") + "\""

    /// Emission cap on a baked chunk table. Beyond this the tables stop being
    /// a good idea (compile time, object size) and the answer is a differently
    /// chunked store, not a bigger literal.
    let private maxBakedChunks = 1_000_000

    /// `static const T name[n] = { ... };`, wrapped so a big table is still a
    /// readable diff. A trailing comma before `}` is well-formed C++.
    let private bakedTable (decl: string) (perLine: int) (items: string list) : string list =
        [ decl + " {" ]
        @ (items |> List.chunkBySize perLine |> List.map (fun g -> "    " + String.concat ", " g + ","))
        @ [ "};" ]

    /// Icechunk's chunk fetch: a compile-time-BAKED table indexed by the
    /// flattened chunk-grid coordinate. The generated program contains no
    /// icechunk logic -- no FlatBuffers, no zstd, no ref resolution -- because
    /// the resolved snapshot is immutable, so its chunk table is a constant.
    ///
    /// Per emitted variable `v`, over a grid of N chunks (tables always
    /// declared with at least one entry, since a zero-length array is
    /// ill-formed C++):
    ///
    ///     static const long long v_icoff[N]            byte offset; -1 marks FILL
    ///     static const char* const v_icfile[N]         chunk-file path, "" if not native
    ///                                                  -- emitted only if some chunk is native
    ///     static const unsigned char v_icb<k>[L]       one array per INLINE chunk, in
    ///                                                  ascending flat-index order
    ///     static const unsigned char* const v_icinl[N] inline pointer or nullptr
    ///                                                  -- emitted only if some chunk is inline
    ///
    /// There is deliberately no per-chunk LENGTH table: every present chunk's
    /// declared length is checked against the padded chunk size at compile time
    /// below, so the length is one baked literal, not N table entries.
    ///
    /// `Present` is `v_icoff[v_cidx] >= 0`, so fill is a table lookup rather
    /// than a failed file open; the shared core still owns the grid loops,
    /// edge intersection, the fill branch body and the flat scatter.
    ///
    /// Every chunk's declared byte length is validated against the padded
    /// chunk size HERE, at compile time -- the runtime check that survives is
    /// for a file that changed under the binary (a GC'd pinned snapshot),
    /// which dies loudly, never as silent zeros.
    let icechunkChunkFetch (ra: ResolvedArray) : ZarrProvider.CppZarr.ChunkFetchEmitter =
        fun v chunkBytes ->
            let meta = ra.Meta
            let varName = meta.Name
            let rank = meta.Shape.Length
            let lens = ZarrProvider.gridDims meta.Shape meta.Chunks |> List.map int
            let strides = ZarrProvider.rowMajorStrides lens
            let n = ra.Table.Length
            if n > maxBakedChunks then
                failwith $"icechunk codegen: variable '{varName}' has {n} chunks, past the {maxBakedChunks}-entry baked-table cap -- store it with larger chunks"
            ra.Table
            |> Array.iteri (fun i loc ->
                match loc with
                | Fill -> ()
                | Inline bytes when bytes.Length <> chunkBytes ->
                    failwith $"icechunk codegen: variable '{varName}': the inline chunk at flat index {i} holds {bytes.Length} bytes, but a padded chunk of this array is {chunkBytes} -- a compressed or corrupt store?"
                | Native nc when nc.Length <> int64 chunkBytes ->
                    failwith $"icechunk codegen: variable '{varName}': the chunk at flat index {i} declares a {nc.Length}-byte range, but a padded chunk of this array is {chunkBytes} bytes -- a compressed or corrupt store?"
                | _ -> ())

            let tn = max 1 n
            let entry (f: ChunkLoc -> string) (dflt: string) =
                [ for i in 0 .. tn - 1 -> if i < n then f ra.Table.[i] else dflt ]

            let anyNative = ra.Table |> Array.exists (function Native _ -> true | _ -> false)
            let anyInline = ra.Table |> Array.exists (function Inline _ -> true | _ -> false)

            // One byte array per inline chunk, named by its ORDINAL among the
            // inline chunks (ascending flat index), plus the flat -> ordinal map.
            let inlineOrdinal = Collections.Generic.Dictionary<int, int>()
            let inlineBlocks =
                [ for i in 0 .. n - 1 do
                    match ra.Table.[i] with
                    | Inline bytes ->
                        let k = inlineOrdinal.Count
                        inlineOrdinal.[i] <- k
                        yield!
                            bakedTable
                                $"static const unsigned char {v}_icb{k}[{bytes.Length}] ="
                                16
                                (bytes |> Array.map (sprintf "0x%02x") |> Array.toList)
                    | _ -> () ]

            let offTable =
                bakedTable $"static const long long {v}_icoff[{tn}] =" 12
                    (entry (function
                            | Fill -> "-1"
                            | Inline _ -> "0"
                            | Native nc -> string nc.Offset) "-1")
            let fileTable =
                if not anyNative then []
                else
                    bakedTable $"static const char* const {v}_icfile[{tn}] =" 4
                        (entry (function
                                | Native nc -> cppPathLit (nativeChunkFile ra.Root nc)
                                | _ -> "\"\"") "\"\"")
            let inlineTable =
                if not anyInline then []
                else
                    bakedTable $"static const unsigned char* const {v}_icinl[{tn}] =" 8
                        ([ for i in 0 .. tn - 1 ->
                            match inlineOrdinal.TryGetValue i with
                            | true, k -> $"{v}_icb{k}"
                            | _ -> "nullptr" ])

            let (refKind, refName) = ra.Ref
            let idx = $"{v}_cidx"
            let fileExpr = $"{v}_icfile[{idx}]"
            let offExpr = $"{v}_icoff[{idx}]"
            let inlExpr = $"{v}_icinl[{idx}]"

            let inlineRead (ind: string) =
                [ ind + $"{{ const unsigned char* {v}_isrc = {inlExpr};"
                  ind + $"  char* {v}_idst = (char*){v}_cbuf;"
                  ind + $"  for (long long {v}_ib = 0; {v}_ib < {chunkBytes}LL; {v}_ib++) {v}_idst[{v}_ib] = (char){v}_isrc[{v}_ib]; }}" ]
            let nativeRead (ind: string) =
                [ ind + $"std::ifstream {v}_cf({fileExpr}, std::ios::binary);"
                  ind + $"if (!{v}_cf) {{ std::cerr << \"Icechunk error: chunk file '\" << {fileExpr} << \"' of '{varName}' cannot be opened -- an expired or garbage-collected snapshot?\" << std::endl; std::exit(1); }}"
                  ind + $"{v}_cf.seekg((std::streamoff){offExpr});"
                  ind + $"{v}_cf.read((char*){v}_cbuf, {chunkBytes});"
                  ind + $"if ({v}_cf.gcount() != (std::streamsize){chunkBytes}) {{ std::cerr << \"Icechunk error: chunk file '\" << {fileExpr} << \"' of '{varName}' is short: expected {chunkBytes} bytes at offset \" << {offExpr} << std::endl; std::exit(1); }}" ]

            let readLines (ind: string) =
                match anyInline, anyNative with
                | true, true ->
                    [ ind + $"if ({inlExpr} != nullptr) {{" ]
                    @ inlineRead (ind + "    ")
                    @ [ ind + "} else {" ]
                    @ nativeRead (ind + "    ")
                    @ [ ind + "}" ]
                | true, false -> inlineRead ind
                | false, true -> nativeRead ind
                | false, false ->
                    [ ind + $"// every chunk of '{varName}' is fill in this snapshot: nothing to read" ]

            let flatExpr =
                if rank = 0 then "0"
                else [ for d in 0 .. rank - 1 -> $"{v}_c{d} * {strides.[d]}" ] |> String.concat " + "

            let identExpr =
                let parts = [ for d in 0 .. rank - 1 -> $"std::to_string({v}_c{d})" ]
                if List.isEmpty parts then "std::string(\"[]\")"
                else "std::string(\"[\") + " + String.concat " + std::string(\", \") + " parts + " + std::string(\"]\")"

            { Prologue =
                [ $"// Read {varName} from icechunk repo {normPath ra.Root} ({kindToken refKind}:{refName}, snapshot {base32Encode ra.SnapshotId})"
                  $"// {n} chunk(s), baked at compile time: the snapshot is immutable, so this table is a constant." ]
                @ offTable @ fileTable @ inlineBlocks @ inlineTable
              Locate = fun ind -> [ ind + $"size_t {idx} = {flatExpr};" ]
              Present = $"{offExpr} >= 0"
              Read = readLines
              Ident = identExpr }

    /// Resolve a variable for emission, or die with the reason at the read site.
    let private resolveOrFail (what: string) (path: string) (varName: string) : ResolvedArray =
        match resolveArray path varName with
        | Ok ra -> ra
        | Error e -> failwith $"icechunk {what} of variable '{varName}' from '{path}': {e}"

    /// Dense reader: the shared assembly core over the baked chunk table into
    /// `<v>_flat`, then the same materialization CppNetcdf/CppZarr do (nested
    /// Array via allocate<>, flat->nested copy, buffers released).
    let genReadVar (path: string) (varName: string) (cppVarName: string) (arrType: IRArrayType) : string list =
        let ra = resolveOrFail "read" path varName
        if ra.Meta.Blade.IsSome then
            failwith $"icechunk codegen: variable '{varName}' is blade-packed; the dense reader cannot materialize it (this indicates a typing inconsistency)"
        let v = cppVarName
        let elemCpp = elemCppOf arrType.ElemType
        let assemble = ZarrProvider.CppZarr.genAssembleFlatVia (icechunkChunkFetch ra) ra.Meta v elemCpp
        let shape = ra.Meta.Shape |> List.map int
        let rank = shape.Length
        let extentDecls = shape |> List.mapi (fun i n -> $"size_t {v}_extent_{i} = {n};")
        let extentNames = shape |> List.mapi (fun i _ -> $"{v}_extent_{i}")
        let idxVars = [ for i in 0 .. rank - 1 -> $"{v}_i{i}" ]
        let openLoops =
            idxVars |> List.mapi (fun d iv ->
                let ind = String.replicate d "    "
                $"{ind}for (size_t {iv} = 0; {iv} < {extentNames.[d]}; {iv}++) {{")
        let nestedSub = idxVars |> List.map (sprintf "[%s]") |> String.concat ""
        let flatIdx =
            let mutable acc = idxVars.[0]
            for i in 1 .. rank - 1 do
                acc <- $"({acc}) * {extentNames.[i]} + {idxVars.[i]}"
            acc
        let bodyInd = String.replicate rank "    "
        let materialize =
            extentDecls
            @ [ $"""size_t {v}_extents[] = {{ {(String.concat ", " extentNames)} }};"""
                $"Array<{elemCpp}, {rank}> {v} = {{ allocate<typename promote<{elemCpp}, {rank}>::type, nullptr>({v}_extents), {v}_extents }};" ]
            @ openLoops
            @ [ $"{bodyInd}{v}{nestedSub} = {v}_flat[{flatIdx}];" ]
            @ [ for d in rank - 1 .. -1 .. 0 -> $"""{(String.replicate d "    ")}}}""" ]
            @ [ $"delete[] {v}_flat;" ]
        assemble @ materialize

    /// Packed (SymIdx/AntisymIdx) and orbit (OrbIdx) reader. The store's pool
    /// IS the in-memory representation, so assembly is the ordinary flat chunk
    /// walk through the shared core; the codegen intercept owns allocation and
    /// copy, so this emits `<v>_flat` and nothing else.
    ///
    /// Two shapes refuse loudly rather than half-work: the 'packed-blocks'
    /// layout, whose per-block assembler is NOT routed through the shared core
    /// (plan §7, P0 outcome (a)), and `read_window`, whose sub-simplex
    /// extraction sits above the core. Both are phase P4.
    let genReadPacked (path: string) (varName: string) (cppVarName: string) (arrType: IRArrayType) (opts: Blade.ProviderRegistry.PackedReadOpts) : string list =
        let ra = resolveOrFail "packed read" path varName
        let meta = ra.Meta
        match meta.Blade with
        | None ->
            failwith $"icechunk codegen: variable '{varName}' has no blade packed layout but was typed packed (this indicates a typing inconsistency)"
        | Some layout when layout.Blocks.IsSome ->
            failwith $"icechunk codegen: variable '{varName}' is stored with the 'packed-blocks' layout, whose per-block chunk I/O does NOT route through the shared chunk-source core (docs/plans/plan-icechunk-provider.md §7, P0 outcome (a)) -- merging the two assemblers is phase P4; store the variable with layout 'packed' to read it from an icechunk repo today"
        | Some _ when opts.Window.IsSome ->
            failwith $"icechunk codegen: variable '{varName}': read_window needs the window-extraction emitter that sits ABOVE the shared chunk-source core, which the icechunk reader does not reach yet (docs/plans/plan-icechunk-provider.md §12, phase P4) -- read the whole pool and take the sub-simplex in Blade"
        | Some layout when layout.Group.Sym = SymWreath ->
            // Everything that could go wrong is a MISMATCH between the declared
            // class and the stored one, checked here rather than trusted.
            let g = layout.Group
            (match arrType.IndexTypes with
             | [ lead ] when lead.Symmetry = SymWreath ->
                 let declLevels = Blade.IR.orbitLevelsOf lead
                 let declExtent =
                     match Blade.IR.orbitBaseExtent lead with
                     | IRLit (IRLitInt n) -> n
                     | _ -> -1L
                 if declLevels <> g.Levels || declExtent <> g.Extent then
                     failwithf "icechunk codegen: variable '%s': declared OrbIdx<%s, %d> does not match the store's orbit head OrbIdx<%s, %d>"
                               varName (Blade.IR.ppOrbitLevels declLevels) declExtent
                               (Blade.IR.ppOrbitLevels g.Levels) g.Extent
             | _ ->
                 failwith $"icechunk codegen: variable '{varName}': the store declares an orbit (iterated-wreath) head, so the variable must type as a SOLE OrbIdx group")
            if opts.Distribute then
                failwith $"icechunk codegen: variable '{varName}': the MPI-distributed read is not defined for an OrbIdx (iterated-wreath) pool (spec_version 2 is the flat single-pool layout only)"
            ZarrProvider.CppZarr.genAssembleFlatVia (icechunkChunkFetch ra) meta cppVarName (elemCppOf arrType.ElemType)
        | Some layout ->
            let g = layout.Group
            (match arrType.IndexTypes with
             | lead :: rest ->
                 let leadOk =
                     lead.Symmetry = g.Sym && lead.Rank = g.Rank
                     && (match lead.Extent with IRLit (IRLitInt n) -> n = g.Extent | _ -> false)
                 let restExtents =
                     rest |> List.map (fun ix ->
                         match ix.Extent with IRLit (IRLitInt n) -> n | _ -> -1L)
                 let restOk =
                     (rest |> List.forall (fun ix -> ix.Symmetry = SymNone && ix.Rank = 1))
                     && restExtents = layout.DenseDims
                 if not (leadOk && restOk) then
                     failwithf "icechunk codegen: variable '%s': declared packed type does not match the store's blade layout (group %A rank %d expected lead extent %d, dense %A)"
                         varName g.Sym g.Rank g.Extent layout.DenseDims
             | [] -> failwith $"icechunk codegen: variable '{varName}': packed read with no index types")
            // opts.Distribute is ignored for the flat pool layout, exactly as
            // the Zarr provider does: only the blocks assembler is rank-scoped.
            ZarrProvider.CppZarr.genAssembleFlatVia (icechunkChunkFetch ra) meta cppVarName (elemCppOf arrType.ElemType)

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
    // via the shared chunk-source core).
    GenReadPacked = Some CppIcechunk.genReadPacked
    ReadWreathPool = Some readWreathPool
    GenReadCompoundVar = None  // load_compound: refused loudly, as Zarr
    GenWriteVar = CppIcechunk.genWriteVar
    GenStreamOpen = None       // `.stream`: deferred (the baked table makes fiber reads easy later)
    GenStreamFiber = None
    Includes = CppIcechunk.genIncludes
    VarDimNames = fun path varName ->
        // Must not throw on an unreadable store: writers fall back to
        // synthesized dim<i> names when this yields None. The zarr.json is
        // authoritative (it is what builds the module); the snapshot's
        // structural names stand in when the JSON carries none.
        try
            match load path with
            | Ok (LoadedCheckout ck) ->
                match findArray ck varName with
                | Ok (node, meta) ->
                    match meta.DimNames with
                    | Some ns -> Some ns
                    | None -> node.DimensionNames
                | Error _ -> None
            | _ -> None
        with _ -> None
    Fingerprint = fingerprint
    VersionStamp = versionStamp
    LinkNeeds = "none (pure std C++17)"
}
