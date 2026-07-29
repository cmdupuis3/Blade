/// The ML module's static-evaluation layer: StaticValue <-> spec/config
/// conversions and the sizing builtins (sh_spec, total_dim, tp_weight_dim,
/// linear_weight_dim, tp_spec, hom_dim), registered into the core evaluator
/// through StaticEval's external-builtin registry so StaticEval.fs itself
/// stays ML-free.
///
/// `install()` is idempotent and is invoked at the top of
/// MLElaborate.expand — the first ML-aware stop of every compilation — so
/// the builtins are visible to every later resolveStatics pass (the
/// elaborator's own, checkModule's, and Lowering's Phase 0).
module Blade.ML.Statics

open Blade.StaticEval
open Blade.ML.Spec
open Blade.ML.PermSpec
open Blade.ML.PointSpec

/// Convert a static value to an ML irreps spec: array of (l, parity, mult)
/// int triples, parity 0 = even / 1 = odd.
let specOfStatic (what: string) (v: StaticValue) : Result<Spec, string> =
    let entryOf (e: StaticValue) =
        match e with
        | SVTuple [ SVInt l; SVInt p; SVInt m ] ->
            if l < 0L then Error (sprintf "%s: l must be >= 0" what)
            elif p <> 0L && p <> 1L then Error (sprintf "%s: parity must be 0 (even) or 1 (odd)" what)
            elif m < 1L then Error (sprintf "%s: multiplicity must be >= 1" what)
            else Ok ({ L = int l; Parity = int p; Mult = int m } : SpecEntry)
        | _ -> Error (sprintf "%s: spec entries must be (l, parity, mult) int triples" what)
    match v with
    | SVTuple entries when not entries.IsEmpty ->
        entries |> List.fold (fun acc e ->
            acc |> Result.bind (fun es -> entryOf e |> Result.map (fun x -> es @ [x])))
            (Ok [])
    | _ -> Error (sprintf "%s: expected a static array of (l, parity, mult) triples" what)

/// Convert a static (spec1, spec2, specOut) triple to a TP config.
let cfgOfStatic (what: string) (v: StaticValue) : Result<TPConfig, string> =
    match v with
    | SVTuple [ s1; s2; so ] ->
        specOfStatic (what + " spec1") s1 |> Result.bind (fun a ->
        specOfStatic (what + " spec2") s2 |> Result.bind (fun b ->
        specOfStatic (what + " specOut") so |> Result.map (fun c ->
            ({ Spec1 = a; Spec2 = b; SpecOut = c } : TPConfig))))
    | _ -> Error (sprintf "%s: expected a static (spec1, spec2, specOut) triple" what)

// ---------------------------------------------------------------------------
// The POINT-GROUP block-spec member (plan-transforms-as-types §3.6, stage
// 5b-i). Twins of the two decoders above, deliberately separate: a point-group
// spec names its blocks by frozen character-table LABEL, not by (l, parity),
// and every diagnostic wants to say the group and quote its roster.
// ---------------------------------------------------------------------------

/// Resolve a point-group NAME against the frozen registry. The unknown-group
/// diagnostic lives HERE (rather than at `PointSpec.pointGroup`, which
/// failwithf's because reaching it with an unregistered name is a compiler
/// bug) — this is the user-facing boundary, and it names the roster.
let pgGroupByName (what: string) (name: string) : Result<PointGroup, string> =
    if List.contains name pointGroupNames then Ok (pointGroup name)
    else
        Error (sprintf "%s: '%s' is not a registered point group — the registry is {%s}. The roster boundary is MATRIX RATIONALITY (every generator entry in {0, +-1}), not crystallography"
                   what name (String.concat ", " pointGroupNames))

/// The expression-position spelling of the same thing: a static STRING naming
/// a registered group. (In TYPE position — `PgIrrepsIdx<C4, SPEC>` — the group
/// is a bare identifier and goes straight to `pgGroupByName`.)
let pgGroupOfStatic (what: string) (v: StaticValue) : Result<PointGroup, string> =
    match v with
    | SVString name -> pgGroupByName what name
    | _ ->
        Error (sprintf "%s: GROUP must be a static string naming a registered point group, e.g. \"C4\" — the registry is {%s}"
                   what (String.concat ", " pointGroupNames))

/// Convert a static value to a point-group spec: an array of
/// (LABEL_NAME, multiplicity) tuples against `grp`'s frozen character table.
///
/// THE UNKNOWN-LABEL DIAGNOSTIC LIVES HERE, not in the counting layer: this is
/// the only place that knows the text the user wrote, and the useful thing to
/// say is the group's whole roster (a finite group has three to five labels —
/// listing them IS the fix).
let pgSpecOfStatic (what: string) (grp: PointGroup) (v: StaticValue) : Result<PgSpec, string> =
    let roster = grp.Irreps |> List.map (fun ir -> ir.Name) |> String.concat ", "
    let entryOf (e: StaticValue) =
        match e with
        | SVTuple [ SVString label; SVInt m ] ->
            if not (grp.Irreps |> List.exists (fun ir -> ir.Name = label)) then
                Error (sprintf "%s: '%s' is not a label of point group %s — its labels are {%s}" what label grp.Name roster)
            elif m < 1L then
                Error (sprintf "%s: multiplicity must be >= 1 (label '%s' carries %d)" what label m)
            else Ok ((label, int m) : string * int)
        | _ ->
            Error (sprintf "%s: spec entries must be (LABEL_NAME, mult) tuples — a STRING label from %s's table {%s} and an int multiplicity" what grp.Name roster)
    match v with
    | SVTuple entries when not entries.IsEmpty ->
        entries |> List.fold (fun acc e ->
            acc |> Result.bind (fun es -> entryOf e |> Result.map (fun x -> es @ [x])))
            (Ok [])
    | _ ->
        Error (sprintf "%s: expected a static array of (LABEL_NAME, mult) tuples over point group %s {%s}" what grp.Name roster)

/// The pg twin of `specToStatic`: a point-group spec back out as a static
/// value, (LABEL_NAME, mult) tuples. `SVString` is first-class in StaticEval,
/// so the name surface costs no new static machinery — §3.6's reason for
/// choosing names over indices at the surface, read from the other direction.
let private pgSpecToStatic (s: PgSpec) : StaticValue =
    SVTuple (s |> List.map (fun (label, m) -> SVTuple [ SVString label; SVInt (int64 m) ]))

let private specToStatic (s: Spec) : StaticValue =
    SVTuple (s |> List.map (fun e ->
        SVTuple [ SVInt (int64 e.L); SVInt (int64 e.Parity); SVInt (int64 e.Mult) ]))

let mutable private installed = false

/// Internal static-evaluator name for a sizing builtin. Qualified call sites
/// (`ml.total_dim(...)`) are normalized to this mangled form by MLElaborate,
/// and the registry is keyed by it — so a bare `total_dim(...)` in user
/// source no longer resolves. The ML surface is reachable only through an
/// `import ml` alias, not language-wide. The surface (unmangled) names are
/// listed in MLElaborate.sizingNames; keep the two in sync.
let statName (name: string) : string = "__ml_stat_" + name

/// Register the ML sizing builtins with the core static evaluator.
/// Idempotent; safe to call from multiple entry points.
let install () =
    if not installed then
        installed <- true
        registerStaticBuiltin (statName "sh_spec") (fun args ->
            match args with
            | [ SVInt lmax ] when lmax >= 0L -> Ok (specToStatic (shSpec (int lmax)))
            | _ -> Error "sh_spec: expected a non-negative static int lmax")
        registerStaticBuiltin (statName "total_dim") (fun args ->
            match args with
            | [ spec ] ->
                specOfStatic "total_dim" spec
                |> Result.map (fun s -> SVInt (int64 (totalDim s)))
            | _ -> Error "total_dim: expected one static spec argument")
        registerStaticBuiltin (statName "tp_weight_dim") (fun args ->
            match args with
            | [ cfg ] ->
                cfgOfStatic "tp_weight_dim" cfg
                |> Result.map (fun c -> SVInt (int64 (tpWeightDim c)))
            | _ -> Error "tp_weight_dim: expected one static (spec1, spec2, specOut) argument")
        registerStaticBuiltin (statName "linear_weight_dim") (fun args ->
            match args with
            | [ sIn; sOut ] ->
                specOfStatic "linear_weight_dim specIn" sIn |> Result.bind (fun a ->
                specOfStatic "linear_weight_dim specOut" sOut |> Result.bind (fun b ->
                linearWeightDim a b |> Result.map (int64 >> SVInt)))
            | _ -> Error "linear_weight_dim: expected (specIn, specOut) static arguments")
        // CG-typed contraction surface: the full decomposition spec of
        // s1 ⊗ s2 (merged-canonical, see MLSpec.tpSpec) and the Schur
        // dimension of Hom_G — both pure spec arithmetic, so output types
        // and weight-space sizes are expressible in `let static` land.
        registerStaticBuiltin (statName "tp_spec") (fun args ->
            match args with
            | [ s1; s2 ] ->
                specOfStatic "tp_spec spec1" s1 |> Result.bind (fun a ->
                specOfStatic "tp_spec spec2" s2 |> Result.map (fun b ->
                    specToStatic (tpSpec a b)))
            | _ -> Error "tp_spec: expected (spec1, spec2) static arguments")
        registerStaticBuiltin (statName "hom_dim") (fun args ->
            match args with
            | [ sIn; sOut ] ->
                specOfStatic "hom_dim specIn" sIn |> Result.bind (fun a ->
                specOfStatic "hom_dim specOut" sOut |> Result.map (fun b ->
                    SVInt (int64 (homDim a b))))
            | _ -> Error "hom_dim: expected (specIn, specOut) static arguments")
        registerStaticBuiltin (statName "tp_full_weight_dim") (fun args ->
            match args with
            | [ s1; s2 ] ->
                specOfStatic "tp_full_weight_dim spec1" s1 |> Result.bind (fun a ->
                specOfStatic "tp_full_weight_dim spec2" s2 |> Result.map (fun b ->
                    SVInt (int64 (tpWeightDim { Spec1 = a; Spec2 = b; SpecOut = tpSpec a b }))))
            | _ -> Error "tp_full_weight_dim: expected (spec1, spec2) static arguments")
        // S₂-compacted self-TP weight spaces: the exchange-symmetric and
        // exchange-antisymmetric halves of tp_full_weight_dim(s, s) — the
        // buffer extents of ml.derive_sym_tp / ml.derive_alt_tp. Their sum IS
        // the dense dimension (MLSpec.s2TpSplitIsPartition); either can be 0,
        // and then the corresponding op is a BL4007 at the call site.
        registerStaticBuiltin (statName "sym_tp_weight_dim") (fun args ->
            match args with
            | [ spec ] ->
                specOfStatic "sym_tp_weight_dim" spec
                |> Result.map (fun s -> SVInt (int64 (symTpWeightDim s)))
            | _ -> Error "sym_tp_weight_dim: expected one static spec argument")
        registerStaticBuiltin (statName "alt_tp_weight_dim") (fun args ->
            match args with
            | [ spec ] ->
                specOfStatic "alt_tp_weight_dim" spec
                |> Result.map (fun s -> SVInt (int64 (altTpWeightDim s)))
            | _ -> Error "alt_tp_weight_dim: expected one static spec argument")
        // Symmetric / exterior powers of a spec (plan-transforms-as-types
        // §3.3): the O(3) irrep decomposition of Sym^K(V) / Λ^K(V) by the
        // integer weight-peel, returned as an ordinary SPEC static — so it
        // composes with total_dim / hom_dim / irreps_* and is writable in an
        // IrrepsIdx<> annotation, exactly like tp_spec. K is capped at 4
        // (plan §6.5: multinomial conditioning of the monomial basis degrades
        // beyond, and body-order expansions live at k <= 4).
        let kArg (name: string) (k: int64) : Result<int, string> =
            if k < 1L || k > 4L then
                Error (sprintf "%s: K must be a static int in 1..4 (got %d) — the symmetric-power surface is capped at degree 4 (plan-transforms-as-types §6.5)" name k)
            else Ok (int k)
        let registerPower (name: string) (kind: PowerKind) =
            registerStaticBuiltin (statName name) (fun args ->
                match args with
                | [ spec; SVInt k ] ->
                    specOfStatic name spec |> Result.bind (fun s ->
                    kArg name k |> Result.bind (fun k ->
                        let res = powerSpec kind s k
                        if res.IsEmpty then
                            Error (sprintf "%s: the exterior power is ZERO for K > dim V (K = %d, total_dim = %d) — there is no spec to name" name k (totalDim s))
                        else Ok (specToStatic res)))
                | _ -> Error (sprintf "%s: expected (SPEC, K) static arguments" name))
        registerPower "sym_spec" PowSym
        registerPower "alt_spec" PowAlt
        // The degree-K parameter-count theorem: the free-weight count of a
        // degree-K homogeneous equivariant polynomial map V -> W is
        // dim Hom_G(Sym^K V, W). At K = 2 with SPEC_OUT = tp_spec(S, S) this
        // IS sym_tp_weight_dim(S) — the stage-1 path count and the stage-2
        // character count are the same theorem, pinned against each other in
        // the corpus.
        registerStaticBuiltin (statName "poly_weight_dim") (fun args ->
            match args with
            | [ spec; SVInt k; sOut ] ->
                specOfStatic "poly_weight_dim SPEC" spec |> Result.bind (fun s ->
                specOfStatic "poly_weight_dim SPEC_OUT" sOut |> Result.bind (fun so ->
                kArg "poly_weight_dim" k |> Result.map (fun k ->
                    SVInt (int64 (polyWeightDim s k so)))))
            | _ -> Error "poly_weight_dim: expected (SPEC, K, SPEC_OUT) static arguments")
        // The Sₙ index-action surface's sizing pair (stage 5a-i,
        // plan-transforms-as-types §3.6). NO spec argument: a permutation
        // module is named by its RANK and the node-axis extent alone, which
        // is the whole reason Sₙ is a sibling registry member rather than a
        // second block-spec family.
        //   perm_weight_dim(K, L, N) = dim Hom_{Sₙ}(ℝ^{N^K}, ℝ^{N^L})
        //                            = #partitions of the K+L axis positions
        //   perm_bias_dim(L, N)      = the same at K = 0 (partitions of [L])
        // Both ERROR below N = K+L rather than switching to the truncated
        // lattice — §3.6's no-silent-fork rule, shared with the ops through
        // PermSpec.checkPermSizing so the two never drift.
        registerStaticBuiltin (statName "perm_weight_dim") (fun args ->
            match args with
            | [ SVInt k; SVInt l; SVInt n ] ->
                if k < 0L || l < 0L then
                    Error (sprintf "perm_weight_dim: K and L must be static ints >= 0 (got %d, %d)" k l)
                elif k > 64L || l > 64L || n > 1000000L || n < -1L then
                    Error (sprintf "perm_weight_dim: K, L and N are static ints out of any sane range (got %d, %d, %d)" k l n)
                else
                    let k, l, n = int k, int l, int n
                    checkPermSizing "perm_weight_dim" "K + L" (k + l) n
                    |> Result.map (fun () -> SVInt (int64 (permWeightDim k l n)))
            | _ -> Error "perm_weight_dim: expected (K, L, N) static int arguments")
        registerStaticBuiltin (statName "perm_bias_dim") (fun args ->
            match args with
            | [ SVInt l; SVInt n ] ->
                if l < 0L then
                    Error (sprintf "perm_bias_dim: L must be a static int >= 0 (got %d)" l)
                elif l > 64L || n > 1000000L || n < -1L then
                    Error (sprintf "perm_bias_dim: L and N are static ints out of any sane range (got %d, %d)" l n)
                else
                    let l, n = int l, int n
                    checkPermSizing "perm_bias_dim" "L" l n
                    |> Result.map (fun () -> SVInt (int64 (permBiasDim l n)))
            | _ -> Error "perm_bias_dim: expected (L, N) static int arguments")
        // Block-navigation builtins (IrrepsIdx v3): fully static per-block
        // accessors so users write block-structured loop nests —
        //   x(irreps_offset(spec, b) + mu * irreps_dim(spec, b) + m)
        // — with every offset and bound folding at compile time. Pure
        // StaticEval surface; no codegen involvement.
        registerStaticBuiltin (statName "irreps_len") (fun args ->
            match args with
            | [ spec ] ->
                specOfStatic "irreps_len" spec
                |> Result.map (fun s -> SVInt (int64 s.Length))
            | _ -> Error "irreps_len: expected one static spec argument")
        let registerBlockAccessor name (f: Spec -> int -> int) =
            registerStaticBuiltin (statName name) (fun args ->
                match args with
                | [ spec; SVInt b ] ->
                    specOfStatic name spec |> Result.bind (fun s ->
                        if b < 0L || b >= int64 s.Length then
                            Error (sprintf "%s: block index %d out of range (spec has %d blocks)" name b s.Length)
                        else Ok (SVInt (int64 (f s (int b)))))
                | _ -> Error (sprintf "%s: expected (spec, block) static arguments" name))
        registerBlockAccessor "irreps_l" (fun s b -> s.[b].L)
        registerBlockAccessor "irreps_parity" (fun s b -> s.[b].Parity)
        registerBlockAccessor "irreps_mult" (fun s b -> s.[b].Mult)
        registerBlockAccessor "irreps_dim" (fun s b -> dim s.[b])
        registerBlockAccessor "irreps_offset" (fun s b -> (blockStarts s).[b])
        // ------------------------------------------------------------------
        // The POINT-GROUP sizing surface (plan-transforms-as-types §3.6,
        // stage 5b-i). Every one takes GROUP as its first argument — a static
        // string naming a registered group — and every one returns an INT.
        // NO pg builtin returns a SPEC: §3.6's post-round check defers the
        // spec-valued forms (the pg analogue of tp_spec / sym_spec) to the TP
        // stage, since there is no pg op consuming a derived spec yet.
        //
        // The one that carries the thesis is `pg_hom_dim`: the FS formula
        // Sum_i m_i*n_i*e_i, which on the SAME spec shape [trivial x 1, E x 2]
        // reads 9 at C4 (E complex, e = 2) and 5 at D4 (E real, e = 1) —
        // same dimensions, same R90 generator, one differing input.
        // `pg_irreps_fs` is the e itself, exposed so a user can SEE the factor
        // rather than infer it from a count.
        // ------------------------------------------------------------------
        registerStaticBuiltin (statName "pg_total_dim") (fun args ->
            match args with
            | [ grp; spec ] ->
                pgGroupOfStatic "pg_total_dim GROUP" grp |> Result.bind (fun g ->
                pgSpecOfStatic "pg_total_dim SPEC" g spec |> Result.map (fun s ->
                    SVInt (int64 (pgTotalDim g s))))
            | _ -> Error "pg_total_dim: expected (GROUP, SPEC) static arguments")
        registerStaticBuiltin (statName "pg_hom_dim") (fun args ->
            match args with
            | [ grp; sIn; sOut ] ->
                pgGroupOfStatic "pg_hom_dim GROUP" grp |> Result.bind (fun g ->
                pgSpecOfStatic "pg_hom_dim SIN" g sIn |> Result.bind (fun a ->
                pgSpecOfStatic "pg_hom_dim SOUT" g sOut |> Result.map (fun b ->
                    SVInt (int64 (pgHomDim g a b)))))
            | _ -> Error "pg_hom_dim: expected (GROUP, SIN, SOUT) static arguments")
        registerStaticBuiltin (statName "pg_irreps_len") (fun args ->
            match args with
            | [ grp; spec ] ->
                pgGroupOfStatic "pg_irreps_len GROUP" grp |> Result.bind (fun g ->
                pgSpecOfStatic "pg_irreps_len SPEC" g spec |> Result.map (fun s ->
                    SVInt (int64 s.Length)))
            | _ -> Error "pg_irreps_len: expected (GROUP, SPEC) static arguments")
        let registerPgBlockAccessor name (f: PointGroup -> PgSpec -> int -> int) =
            registerStaticBuiltin (statName name) (fun args ->
                match args with
                | [ grp; spec; SVInt b ] ->
                    pgGroupOfStatic (name + " GROUP") grp |> Result.bind (fun g ->
                    pgSpecOfStatic (name + " SPEC") g spec |> Result.bind (fun s ->
                        if b < 0L || b >= int64 s.Length then
                            Error (sprintf "%s: block index %d out of range (spec has %d blocks)" name b s.Length)
                        else Ok (SVInt (int64 (f g s (int b))))))
                | _ -> Error (sprintf "%s: expected (GROUP, SPEC, BLOCK) static arguments" name))
        // ------------------------------------------------------------------
        // RESTRICTION (plan-equivariance-in-types stage A3). The FIRST
        // spec-valued pg builtin, and the note above earned its exception: the
        // 5b-i round deferred spec-valued pg forms because no pg op consumed a
        // DERIVED spec, and `ml.derive_pg_linear` now does. `pg_restrict` is
        // the only way to NAME the point-group module an O(3) space becomes
        // under the subgroup inclusion, so a form that returned an int would
        // answer a question nobody asked.
        //
        // IT IS A SPEC, NOT A CAST. The result names the decomposition of
        // D^spec restricted along the group's declared O(3) embedding; it does
        // NOT say an `IrrepsIdx<SPEC>` buffer may be READ as a
        // `PgIrrepsIdx<GROUP, pg_restrict(GROUP, SPEC)>` buffer. The two
        // layouts genuinely differ (the O(3) side orders a block by
        // m = -l..l, so the invariant m = 0 component sits in the MIDDLE while
        // a pg block is contiguous) — see MLPointSpec's restriction header.
        // Parity is FORGOTTEN, because both shipped embeddings are proper
        // rotation groups: pg_restrict(G, [(0, 1, 1)]) is the trivial label,
        // i.e. a pseudoscalar becomes a genuine invariant of G.
        registerStaticBuiltin (statName "pg_restrict") (fun args ->
            match args with
            | [ grp; spec ] ->
                pgGroupOfStatic "pg_restrict GROUP" grp |> Result.bind (fun g ->
                specOfStatic "pg_restrict SPEC" spec |> Result.map (fun s ->
                    pgSpecToStatic (restrictSpec g (s |> List.map (fun e -> (e.L, e.Parity, e.Mult))))))
            | _ -> Error "pg_restrict: expected (GROUP, SPEC) static arguments — GROUP a registered point group, SPEC an O(3) irreps spec of (l, parity, mult) triples")
        registerPgBlockAccessor "pg_irreps_dim" (fun g s b -> (pgIrrep g (fst s.[b])).DimR)
        registerPgBlockAccessor "pg_irreps_mult" (fun _ s b -> snd s.[b])
        registerPgBlockAccessor "pg_irreps_fs" (fun g s b -> endDim (pgIrrep g (fst s.[b])).Fs)
        registerPgBlockAccessor "pg_irreps_offset" (fun g s b -> (pgBlockStarts g s).[b])
