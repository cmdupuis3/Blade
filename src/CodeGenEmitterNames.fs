/// THE EMITTER'S OWN NAMES (borrow round, lane x4). Every identifier the C++
/// generators (src/CodeGen*.fs) and the provider emitters (src/providers/*.fs,
/// src/RunRecord.fs, src/display/DisplayFrame.fs) write into the translation unit
/// that begins with `__`: the FIXED spellings (`__fk`, the flat sweep's loop
/// index) and the FAMILIES, a spelling with holes the emitter fills with a number
/// or a word (`__i<d>`, `__range<i>_<name>`, `__dc<name>_g<g>_<k>`).
///
/// A user binding, function, parameter or capture spelled like one of them met
/// it in the emitted C++ and was SHADOWED by it: `let __fk: Float64 = 10.0` read
/// inside a map's kernel read the sweep's index instead ([1.0, 3.0, 5.0] for
/// [11.0, 12.0, 13.0], with no error). `CodeGenState.installCppNameTable` counts
/// the OPEN names as RESERVED, so such a spelling moves away exactly as one a
/// runtime header declares does. Nothing marks a spelling as user-written (the
/// front end mints `__` names too), so the move is by COLLISION, never by the
/// leading `__`: a spelling that is not an emitter name keeps its spelling.
///
/// EVERY NAME IS IN EXACTLY ONE TABLE:
///   OPEN (the default; `fixedNames`, `familyPatterns`, `builtInSteps`): declared
///     in a C++ scope that also holds emitted user code (a kernel, a key or
///     predicate call, a binding's storage) or a user declaration (main, a
///     function body). These are reserved.
///   CLOSED (`closedNames`): declared only inside a helper whose body holds no
///     emitted user code and no user declaration, over its own parameters; each
///     row states that scope argument in one line. Not reserved.
///   MINTED AVOIDING (`mintedAvoiding`): the emitter mints the name stepping
///     aside from the program's spellings itself; each row says how. Not reserved.
///   COUPLINGS (`couplings`): a name a front-end pass writes MEANING the
///     emitter's variable of that spelling (both ends named). Not reserved.
/// tests/EmitterNameTests.fs (`blade test emitter-names`, and the default suite's
/// "Emitter Names Sync") checks the generator sources against these tables: a
/// name a generator writes that no table holds, or that two hold, fails it.
module Blade.CodeGenEmitterNames

open System.Text.RegularExpressions

/// Spellings under these prefixes are the emitter's (`__blade_t0` of the timing
/// scaffold, `blade_member_init_i64`, a CUDA device pointer `__blade_d_<name>`).
let emitterReservedPrefix (s: string) = s.StartsWith "__blade_" || s.StartsWith "blade_"

/// OPEN fixed spellings (outside the reserved prefixes).
let fixedNames : string list = [
    "__BLADE_FRAME_END__"; "__BLADE_FRAME__"; "__a"; "__acc"; "__args"; "__b"; "__b1"; "__bm"; "__bo"
    "__byOuter"; "__c"; "__c_hi"; "__c_lo"; "__call"; "__cbe"; "__cbk"; "__cep"; "__cep_b"; "__cep_e"
    "__cep_ls"; "__choice_l"; "__ci"; "__crk"; "__crl0"; "__crl1"; "__crl2"; "__crl3"; "__crl4"; "__crl5"
    "__crl6"; "__crl7"; "__crn"; "__crp"; "__crx"; "__cs"; "__cv"; "__cx"; "__d"; "__dT"; "__dacc"; "__dci"
    "__dcj"; "__dcz0"; "__dcz1"; "__declspec"; "__device__"; "__dlo"; "__dn"; "__dpart"; "__ek"; "__expr"
    "__f0"; "__f1"; "__fb_t"; "__ffirst"; "__first"; "__fk"; "__fnbody_group_n"; "__fp_cells"; "__fr_i"
    "__fr_pool"; "__g"; "__g249"; "__gacc"; "__gath"; "__gi"; "__gj"; "__gjr"; "__gk"; "__global__"
    "__gres"; "__growi"; "__growj"; "__gspan"; "__gv_base"; "__gx"; "__halowin"; "__has_include"; "__hi"
    "__hi0"; "__hi1"; "__host__"; "__hr"; "__i"; "__i0"; "__i1"; "__ib"; "__ie"; "__j"; "__ja0"; "__jb"
    "__je"; "__jk"; "__jm"; "__jn"; "__k"; "__ka"; "__kb"; "__ki"; "__lambda"; "__launch_"; "__len"
    "__lo"; "__lo0"; "__lo1"; "__ma"; "__mbrow"; "__mcrow"; "__mi"; "__mj"; "__mp"; "__ms"; "__mt"; "__n"
    "__nt"; "__o"; "__oT"; "__oTact"; "__ob"; "__oc1"; "__of0"; "__off"; "__ohi"; "__ok"; "__olen"
    "__olo"; "__omp_distinct"; "__omp_maxth"; "__omp_seen"; "__omp_team"; "__omp_teamsz"; "__ont"; "__oo"
    "__opart"; "__opool"; "__orbc"; "__orbk"; "__orbrd"; "__ori"; "__orow"; "__ot"; "__ototal"; "__out"
    "__ow_i"; "__ox"; "__oxn"; "__p"; "__pc_i"; "__pc_pool"; "__perm"; "__pi"; "__pl"; "__pn"; "__pool"
    "__ps"; "__ps_c"; "__ps_cell"; "__ps_extra"; "__ps_fail"; "__ps_grant"; "__ps_hi"; "__ps_i"; "__ps_lo"
    "__ps_next"; "__ps_plan"; "__ps_primary"; "__ps_remaining"; "__ps_state"; "__ps_status"; "__ps_step"
    "__ps_term"; "__pt"; "__pv"; "__q"; "__r"; "__rT"; "__rTact"; "__racc"; "__rbody"; "__rcnt"; "__rcres"
    "__rec"; "__retdef"; "__rhad"; "__rhas"; "__rhi"; "__ri"; "__rj"; "__rk"; "__rlane"; "__rlo"; "__rlo0"
    "__rn"; "__rnt"; "__rowc"; "__rowd"; "__rows"; "__rowv"; "__rp_emit"; "__rp_first"; "__rp_i0"; "__rp_k"
    "__rp_row"; "__rpart"; "__rrow"; "__rsi"; "__rt"; "__run_hi"; "__run_lo"; "__run_next"; "__run_remaining"
    "__run_step"; "__rv"; "__rx"; "__s"; "__sbig"; "__seg"
    "__sf"; "__si"; "__sib_c"; "__sj"; "__sk"; "__skk"; "__sm"; "__so"; "__sp"; "__sparse_r"; "__ss"; "__st"
    "__sx"; "__sxt"; "__sy_i"; "__sy_j"; "__sz"; "__t"; "__t0"; "__t1"; "__tbase"; "__tbytes"; "__tn"; "__tt"
    "__tv"; "__ui"; "__v"; "__view"; "__w"; "__w_hi"; "__w_lo"; "__whi"; "__wi"; "__wlo"; "__x" ]

/// OPEN families (outside the reserved prefixes). Holes: `#` the emitter fills
/// with a NUMBER (an IR id, a level, a position, a counter); `@` with a
/// BINDING'S EMITTED SPELLING (the binding the code is emitted for: `__dc<v>_a`
/// beside `v`) -- a member is reserved only when that word is a spelling of
/// this program (`isEmitterName`'s `programWord`), since otherwise no emitted
/// name of the family is that member; `*` with any other word (a plan tag), any
/// run of identifier characters.
let familyPatterns : string list = [
    "__af_*"; "__ak_*"; "__apool_*"; "__aw#o_*"; "__aw#p_*"; "__bfh#_@"; "__bl_member_#_#"
    "__cep_T_*"; "__cep_acc_*"; "__cep_had_*"; "__cep_part_*"; "__cfc_#"; "__cpsrc_*"; "__cp#"; "__crc#_*"
    "__crl#"; "__cuda_@"; "__c#"
    "__dc@_F"; "__dc@_S#"; "__dc@_a"; "__dc@_b"; "__dc@_g#_#"; "__dc@_g#_#_log"; "__dc@_p"; "__dc@_p#"
    "__dc@_t"; "__dc@_z"; "__dc@_*#"; "__d#"
    "__epl_acc_*_#"; "__epl_ls_*_#"; "__epl_part_*_#"; "__fb_t*"; "__fb#"; "__fp_@"; "__f#"
    "__gacc#"; "__gk#"; "__gk#__ngroups"; "__gk#__offsets"; "__growj#"; "__g#"; "__hr_@"; "__hrc_@"; "__i#"
    "__jT_*"; "__ja#"; "__jhad_*"; "__jn_*"; "__jn@_#"; "__jpart#_*"; "__jp#_*"; "__jr#_#"; "__jshr#"
    "__jsrc#_*"; "__jx#"; "__kv#"; "__launch_@"; "__launch_@_begin"; "__launch_@_end"; "__match_#"
    "__oa#"; "__oc#"; "__odc@_c"; "__odc@_#"; "__oend_*_#"; "__ohi_*_#"; "__omp_maxth_*"; "__omp_seen_*"
    "__omp_team_*"; "__ooff_*_#"; "__opool_*"; "__orb_@_#"; "__ord#"; "__orow_@"; "__ot#"
    "__pa#"; "__pg#"; "__pr#"; "__p#"; "__p#_a#"
    "__rT_*"; "__racc_*"; "__range#"; "__range#_r#"; "__range#_@"; "__retv#"; "__ret#"; "__rev#"; "__rhad_*"
    "__rhas_*"; "__rlane#"; "__rn_*"; "__rng#"; "__rp_i#"; "__rp_row*"; "__rpart_*"; "__rsT_#"; "__rslot_*"
    "__rsn_#"; "__rsnt_#"; "__rspart_#"; "__rsrc#"; "__rsrc#_*"; "__rsslot_#"; "__rsum#"; "__r#"
    "__sf_*"; "__sga#_first"; "__sga#_k"; "__sga#_state"; "__sibpf#"; "__sibpf#_#"; "__sk#_*"; "__sk@_#"
    "__spool_*"; "__sq@_#"; "__srun_*"; "__tup_#"; "__tv#"; "__tw#"; "__t#"; "__t@_#"; "__v#__def"
    "__wrap_#"; "__wrap_#_*"; "__wsrc#"; "__z#"; "__@_bound"; "__@_k"; "__@_x" ]

/// OPEN NAMES BUILT IN MORE THAN ONE STEP, which the source extraction cannot
/// see whole: the token the source writes, and the family it begins. A token
/// that ends a string literal continued by `+` must have a row here.
let builtInSteps : (string * string) list = [
    "__gath", "__gath*_#"           // CodeGenExpr: "__gath" + fnv text + "_" + string i
    "__rcall", "__rcall*"           // CodeGenExpr: "__rcall" + fnv text
    "__omp_maxth", "__omp_maxth*"   // CodeGenLoopNest: "__omp_maxth" + covSfx
    "__omp_seen", "__omp_seen*"
    "__omp_team", "__omp_team*" ]

/// CLOSED names: declared only inside a helper whose body holds no emitted user
/// code and no user declaration, over its own parameters (pattern, the scope
/// argument). Not reserved.
let closedNames : (string * string) list = [ ]

/// Names the emitter MINTS AVOIDING the program's spellings (pattern, how).
/// Not reserved: the minted side steps aside, so the user spelling keeps its
/// emission.
let mintedAvoiding : (string * string) list = [
    "__v#", "CodeGenState.localVarName: avoidUserSpelled against the program's spellings"
    // CodeGen.fs `free` (the same-cell return kernel): the stem, else the stem
    // plus 1, 2, ... -- the first spelling neither in the scope's names map nor a
    // token of the emitted body
    "__sc_pool", "CodeGen.fs `free`: past the scope's names and every body token"
    "__sc_cells", "CodeGen.fs `free`: past the scope's names and every body token"
    "__sc_k", "CodeGen.fs `free`: past the scope's names and every body token"
    "__sc_old", "CodeGen.fs `free`: past the scope's names and every body token" ]

/// COUPLINGS BY NAME: a front-end pass writes a spelling MEANING the emitter's
/// variable of that spelling (front-end pass, spelling pattern, emitter site).
/// Not reserved: moving it would cut the coupling.
let couplings : (string * string * string) list = [ ]

/// The regular expression of a pattern (`#` digits, `*` and `@` identifier
/// characters: a pattern's text, without the `@` word test).
let patternRegexText (p: string) : string =
    p |> Seq.map (fun c ->
            match c with
            | '#' -> "[0-9]+"
            | '*' | '@' -> "[A-Za-z0-9_]+"
            | c -> Regex.Escape(string c))
      |> String.concat ""

let private fixedSet = lazy (System.Collections.Generic.HashSet<string>(fixedNames, System.StringComparer.Ordinal))

let private openPatterns () = (familyPatterns @ List.map snd builtInSteps) |> List.distinct

/// The OPEN families with no `@` hole, as one expression.
let private plainRegex =
    lazy (
        let alts = openPatterns () |> List.filter (fun p -> not (p.Contains "@")) |> List.map patternRegexText
        Regex("^(?:" + String.concat "|" alts + ")$", RegexOptions.CultureInvariant))

/// The OPEN families with an `@` hole (exactly one each): the literal head
/// before the first hole (a cheap filter), the part before `@` anchored at the
/// start, and the part after it anchored at both ends.
let private wordFamilies =
    lazy (
        openPatterns () |> List.filter (fun p -> p.Contains "@") |> List.map (fun p ->
            let k = p.IndexOf '@'
            let left, right = p.Substring(0, k), p.Substring(k + 1)
            let head = left |> Seq.takeWhile (fun c -> c <> '#' && c <> '*') |> Seq.toArray |> System.String
            head,
            Regex("^" + patternRegexText left, RegexOptions.CultureInvariant),
            Regex("^" + patternRegexText right + "$", RegexOptions.CultureInvariant)))

/// Is `s` an OPEN emitter name: a reserved prefix, a fixed name, a member of a
/// family -- of an `@` family only when its word is a spelling of this program
/// (`programWord`)? Every family begins with `__`, so a spelling that does not
/// is decided by the prefix rule alone.
let isEmitterName (programWord: string -> bool) (s: string) : bool =
    emitterReservedPrefix s
    || (s.StartsWith "__"
        && (fixedSet.Force().Contains s
            || plainRegex.Force().IsMatch s
            || wordFamilies.Force() |> List.exists (fun (head, left, right) ->
                s.StartsWith head
                && (let m = left.Match s
                    m.Success
                    && (let i = m.Length
                        seq { i + 1 .. s.Length - 1 }
                        |> Seq.append (if right.IsMatch "" then [ s.Length ] else [])
                        |> Seq.exists (fun j -> programWord (s.Substring(i, j - i)) && right.IsMatch(s.Substring j)))))))

let private wordTailRegex =
    lazy (
        let alts = openPatterns () |> List.filter (fun p -> p.EndsWith "*" || p.EndsWith "@") |> List.map patternRegexText
        Regex("^(?:" + String.concat "|" alts + ")$", RegexOptions.CultureInvariant))

/// Could `s` be a member of a family that ENDS in a word hole (`__af_<word>`,
/// `__fp_<name>`, read with any word)? Every lengthening of such a member with
/// `_` may be a member too (an internal `__` word is a program word of any
/// length), so a spelling that must move does not leave by lengthening
/// (`installCppNameTable` moves it under `u_` instead). No family ends in a
/// literal `_`, so a spelling ending in `_` that is not such a member meets only
/// the finitely many fixed names and program spellings.
let isWordTailMember (s: string) : bool =
    s.StartsWith "__" && wordTailRegex.Force().IsMatch s
