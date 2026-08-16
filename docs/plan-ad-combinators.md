# Plan (draft): AD through loop-object combinators — the C-track

Status: design draft. Date 2026-08-15.

**Cite discipline.** Every `file:line` below was read in the worktree
`.claude/worktrees/forward-mode-ad` at `9cb7a53` (= `db83e85` + the forward-mode
plan doc), with `src/Grad.fs` carrying **uncommitted F0 work** (176 insertions:
the collision gate, the type-alias/module-val context fields, and F0.4's
combinator-operator refusal arm, already landed at `Grad.fs:288-294`). Grad.fs
line numbers therefore differ from `docs/plan-forward-mode-ad.md`'s, which were
verified against master. Where a cite matters and the file is volatile I name
the symbol as well as the line. Everything else (`formalism.md`, `Ast.fs`,
`TypeCheck.fs`, `Interp/Loops.fs`, `ReynoldsCore.fs`, `proofs/BladeJacobian.v`,
corpus) is at `9cb7a53` and stable.

Prior art: `docs/plan-forward-mode-ad.md` §4 F4 (the one paragraph this document
expands); the retired roadmap's C-track quote — *"AD-through-combinators: `<|>`
and `<|:>` are the exceptions — value-branching vs storage-branching; Tier-1
emission = mut-buffer + NFor loops, since pipelines aren't re-differentiable
until C-rules close."* Both of that quote's claims are examined below and one of
them is **refuted** (§2.13/§2.14).

---

## 0. The three constraints that decide every rule

**0.1 The transform is pre-typecheck source-to-source.** `ad.grad` rewrites
surface AST and lets the ordinary pipeline typecheck, lower, and codegen the
result (`Grad.fs:22-25`); jvp keeps that slot (`plan-forward-mode-ad.md:81`,
last elaborator, `TypeCheck.fs:17582`). **Consequence: a rule with no surface
Blade spelling is BLOCKED regardless of how easy the calculus is.** Half the
BLOCKED verdicts below are of exactly this kind — the mathematics is a
one-liner and the language has no way to write it down.

**0.2 Two tiers.** *Tier-2* = the derived program is itself a combinator
program (rides symmetry/omp/BLAS routing). *Tier-1* = the private
`StmtForIn`/mut-buffer lane grad already uses for reduce and recursive arrays
(`Grad.fs:521-528`, `Grad.fs:595-598`; BL1003 is parser-only and the lane is
fully supported downstream, `TypeCheck.fs:6515`). Tier-1 is always available
and is a legitimate answer, not a stopgap.

**0.3 Two pairing encodings.** Forward mode must get `(value, tangent)` to the
same kernel cell. There are exactly two ways:

- **E1 — zip-pairing.** Each differentiable loop *slot* `A` becomes
  `zip(A, __t_A)`; the kernel's parameter list doubles. `zip` co-iterates over
  the shared min-rank prefix and hands the kernel one flat parameter per array,
  or the whole tuple under a `Tuple<n>` annotation (`formalism.md:131`). It does
  **not** change the loop's rank, so the iteration space, S-dims, and output
  shape are preserved exactly.
- **E2 — capture-read.** Kernels that already read arrays *by index* from the
  enclosing scope (halo windows, `range<I>` loops) need no pairing at all: the
  tangent kernel just reads `__t_A` at the same index. `method_for(halo<Idx<5>,
  [-1,0,1]>) <@> lambda(w) -> A(w(-1)) + A(w(0)) + A(w(1))` (`loops/074:6`) is
  the canonical shape.

**E2 is strictly easier than E1** — no parameter-list surgery, no identity-group
risk, no `Tuple<N>` typing. Where a combinator admits E2, the rule is nearly
free. Where only E1 is available, everything in §1 applies.

---

## 1. The symmetry license — and the one refusal that blocks it

`proofs/BladeJacobian.v:365-398` (`tangent_joint_swap`) proves the exact
statement the tangent program needs: if the primal kernel is structurally
symmetric under `a ↔ b`, the tangent kernel `∂ₐe·da + ∂ᵦe·db` is invariant under
the **joint pair swap** `(a,da) ↔ (b,db)` (`jswap`, `:357-358`). Two refutations
fence it in:

- `per_dim_swap_not_symmetry` (BladeCore; `docs/proofs.md:58-61`) — independent
  per-dimension swaps are not an output symmetry. Declaring `comm(a,b)` and
  `comm(da,db)` *separately* is therefore unsound.
- `semantic_hypothesis_insufficient` (`BladeJacobian.v:542-545`, proof
  `compute; lia` giving `0 <> 5`) — a *semantically* symmetric primal can have a
  joint-swap-breaking tangent. The license must come from the **structural**
  judgment (declared/deduced `comm`), never from an observed value symmetry.

**Under E1 the joint pair is exactly a loop slot.** `method_for(zip(A,Ȧ),
zip(B,Ḃ))` puts `(A,Ȧ)` in slot 0 and `(B,Ḃ)` in slot 1; swapping slots drags
value and tangent together. The natural spelling of the license is therefore

```blade
method_for(zip(A, __t_A), zip(B, __t_B))
  <@> lambda(pa: Tuple<2>, pb: Tuple<2>) where comm(pa, pb) -> ...
```

**And that is precisely what the compiler refuses today.**
`tests/corpus/diagnostics/060_tuple_param_with_where_clause.blade:3` pins
BL3999, *"`comm`/`anticomm` clause on a kernel that also declares a `Tuple<N>`
parameter is not supported"*, with the reasoning at `:12-16`: comm groups are
positional, the `Tuple<N>` expansion renumbers positions, the failure would be
**silent** (*"a misplaced comm group degrades to dense storage with no
diagnostic"*), and — the load-bearing sentence — *"there is nothing settled to
remap TO — `comm(p, q)` between two PAIRS has no agreed meaning."*

`tangent_joint_swap` settles the meaning, for this case, with a machine-checked
proof. That is compiler ask #1 (§5).

**The documented escape is unsound here.** `060:26` says *"write the parameters
flat, one per operand."* Flat over two zipped slots gives `lambda(a, da, b, db)`,
where the only available spelling of the license is two independent groups
`comm(a,b), comm(da,db)`. Their closure is S₂×S₂ ⊃ the diagonal S₂ we want, and
the extra elements are exactly the per-dimension swaps `per_dim_swap_not_symmetry`
refutes. **Do not take the escape.**

**Tranche-1 disposition: tangent outputs are DENSE.** The primal loop is
untouched and keeps its triangular storage and r! iteration saving; only the
derived tangent leg goes dense. That is a bounded, ≤ r! cost on one leg, and it
is correct. Symmetry inheritance becomes its own rung (C5), gated on ask #1.

One unverified extrapolation to probe before building: `tuples/011:22` pins one
zip against one `Tuple<2>` parameter; two zips against two `Tuple<2>` parameters
(the shape above) is the natural extension but is **not** pinned anywhere.
Note also that nested tuple *operands* are not co-iteration — `object_for(
lambda(p: Tuple<2>, q: Tuple<2>)) <@> ((A,B),(C,D))` iterates **all four**
arrays as a rank-4 outer product (`tuples/012:47-56`). Only `zip` pairs.

---

## 2. Per-combinator specification

Summary (difficulty: FREE / MECHANICAL / DESIGN / BLOCKED):

| Combinator | FWD | REV | Note |
|---|---|---|---|
| `pure`, `\|> compute` | FREE | FREE | identity / materialization barrier |
| `<*>` | FREE | FREE | structural list concat |
| `stack`, `sequence`, `replicate` | FREE | FREE | adjoint = rank-peel read |
| `join(A,B,d)` | FREE | **BLOCKED** (T2) | no `subset` in the AST |
| `transpose`, `decompact` | FREE | MECHANICAL / T1 | decompact adjoint = orbit sum (mechanized) |
| `guard` | FREE | FREE | predicate not differentiated |
| `<\|:>` (fallback) | **FREE** | MECHANICAL | storage-keyed ⇒ linear |
| `compound` / `mask` | MECHANICAL | MECHANICAL | gather ↔ `<\|:>` scatter |
| `method_for`/`object_for`/`<@>`, `zip` | MECHANICAL | MECH (n=1) / DESIGN (n≥2) | E1 |
| `<&>`, `<&!>` (map form) | MECHANICAL | MECHANICAL | must share one named loop |
| `<&!>` (reduction join) | MECH (additive) / T1 else | MECH (additive) / **BLOCKED** else | no cross-leg coupling |
| `halo` | MECHANICAL | DESIGN / T1 | E2; adjoint needs a Pad boundary |
| `reynolds` | MECHANICAL | DESIGN | joint permutation; recompute the plan |
| `gram` | MECHANICAL | **MECHANICAL** | both adjoints are matmuls (BLAS) |
| `group_by` / `group_keys` | MECHANICAL | **BLOCKED** (T2) | no ungroup surface |
| `sort` | DESIGN | DESIGN | key-over-zip unverified |
| `>>@`, `@>>`, `<$>` | DESIGN | **skip — inline** | Tuple<N> element typing |
| `>>=` | MECHANICAL–DESIGN | skip | it is a `let` in disguise |
| `<\|>` (choice) | **BLOCKED** | **BLOCKED** | discontinuous; **SKIP** (§2.13) |
| `Poly<T^k>` / arity-poly | **BLOCKED** | **BLOCKED** | architecture (§2.19) |
| `align` / `stencil` | n/a | n/a | paper surface, zero users |

### 2.1 `pure` / `|> compute`

1. **Semantics.** `pure : α → Computation α`; `|> compute : Computation α → α`
   materializes with cache-optimal layout; `pure A |> compute ≡ A`
   (`formalism.md:993`, `:119-121`).
2. **FWD.** `pure(e) ⇒ pure(ė)`; `c |> compute ⇒ ċ |> compute`. Identity on
   values; `compute` is a materialization barrier that the tangent mirrors.
3. **REV.** Identity both directions.
4. **Machinery.** None beyond the paired-binding walker.
5. **FREE.**
6. **Storage.** No kernel; nothing to inherit.

Already partly present: `replicate(N, pure(lit)) |> compute` is grad's
whitelisted `ConstFill` (`Grad.fs:265-269`) and `zeroFill` (`:272-274`) is
already the right tangent for it.

### 2.2 `<*>` (array-list product)

1. **Semantics.** `method_for(A) <*> method_for(B) ≡ method_for(A, B)`; purely
   structural shape concatenation with multiplicative cardinality, identity
   `method_for()` (`formalism.md:1031-1036`; `TypeCheck.fs:8640-8664` merges the
   `Arrays`/`Identities`/`SDimsPerArray` lists).
2. **FWD.** Distribute E1 over the legs:
   `method_for(zip(A,Ȧ)) <*> method_for(zip(B,Ḃ))`. Because `<*>` concatenates
   `Identities` verbatim (`:8652`), whatever identity detection the primal got,
   the tangent gets — modulo §1's shared-binding caveat.
3. **REV.** Structural; the adjoint splits the operand list the same way.
4. **Machinery.** None.
5. **FREE.**
6. **Storage.** `<*>` is symmetry-neutral: *"commutativity comes from the kernel
   later"* (`formalism.md:1035-1036`). Inheritance is decided at `<@>`, §2.5.

### 2.3 `stack` / `sequence` / `replicate`

1. **Semantics.** `stack(A,B,C)(k)` selects array k; rank+1 with a **fresh**
   leading symmetry class (`formalism.md:134`). `sequence`/`replicate` are the
   collection-layer twins (`:1097-1098`); the interpreter stacks child rows into
   a rank-added array (`Interp/Loops.fs:494-509`).
2. **FWD.** `stack(Ȧ,Ḃ,Ċ)`, `sequence [ċ₁..]`, `replicate(n, ċ)`. Linear.
3. **REV.** The adjoint is a rank-peel read: `ḡA_k += cot(k)`, which is native
   (arrays are functions; partial application yields a lower-rank view,
   `formalism.md:545-566`). Genuinely combinator-level.
4. **Machinery.** None.
5. **FREE** both directions.
6. **Storage.** The fresh leading class means neither primal nor tangent claims
   symmetry across the stacked axis; nothing to transfer.

**Trap.** `replicate(n, pure(lit))`'s tangent is a **zero** fill, and separately
the `zero` kernel (`formalism.md:1069-1071`) *"resolves to the operation-
appropriate identity (1 under `*`, 0 under `+`)"* — so the tangent of `zero` is
literal `0.0` in both cases, never `zero`. Emitting `zero` into a `*`-recursion
base case would inject a spurious 1. Pin it.

### 2.4 `join` / `transpose` / `decompact`

1. **Semantics.** `join(A,B,d)` concatenates along d (`formalism.md:137`,
   `Ast.fs:454`); `transpose(A,p)` is a hard permutation, the identity on
   symmetric arrays under the identity-under-σ permutation and **negating per
   parity** on antisymmetric ones (`formalism.md:135`, `Ast.fs:487`);
   `decompact(A,axis)` expands a compact axis to dense, sign-correct for
   antisymmetric sources (`formalism.md:140`, `Ast.fs:488`).
2. **FWD.** All three are linear reindexings: apply the same op to the tangent.
   `transpose` and `decompact` must reuse the *same* permutation/axis so the
   antisymmetric sign rule lands identically on both legs.
3. **REV.**
   - `transpose`: adjoint = transpose by the inverse permutation, **carrying the
     same parity sign**. MECHANICAL.
   - `decompact`: the adjoint is the **orbit sum** back onto the canonical cell
     — `cot(p,q) + cot(q,p)` off-diagonal, `cot(p,p)` on-diagonal at r=2. That
     is BladeJacobian's `symmetric_accumulation` multiplicity rule, mechanized
     with concrete n=3 pins `off_diagonal_x2` / `diagonal_x1`
     (`docs/proofs.md:606-614`). The proof exists; the *surface* does not (there
     is no `compact` inverse of `decompact`), so → **Tier-1**.
   - `join`: the adjoint is `subset`, which `formalism.md:137` lists but which
     has **no AST node** (`ExprSubset` does not exist; `Ast.fs:454` has `ExprJoin`
     alone). → **BLOCKED at Tier-2**, Tier-1 slice-write instead.
4. **Machinery.** Parity-aware transpose adjoint; orbit-multiplicity emitter
   (the lane's `+=` already does the accumulation).
5. FWD **FREE**; REV **MECHANICAL** (transpose) / **Tier-1** (decompact) /
   **BLOCKED at Tier-2** (join).
6. **Storage.** Preserved by construction; the antisymmetric parity rule is the
   thing to get wrong.

> **The structural finding this group exposes.** Reverse mode keeps needing the
> *inverse* of a structural op, and **Blade's structural surface is not closed
> under inversion**: `join` without `subset`, `halo` without `Pad`, `group_by`
> without ungroup, `decompact` without `compact`. Forward mode never needs an
> inverse. This asymmetry — not tape management, not kernel calculus — is the
> reason reverse-through-combinators is mostly Tier-1, and it vindicates the
> retired quote's "Tier-1 = mut-buffer + NFor" default.

### 2.5 `method_for` / `object_for` / `<@>` — the central rule

1. **Semantics.** `MethodLoop × Function → Computation` and
   `ObjectLoop × A* → Computation`; the two curryings are forced (identity
   detection needs all arrays, commutativity detection needs the kernel)
   (`formalism.md:713-733`, `:989`). Kernels live in T-world and see slices,
   never S-dims; `comm` is metadata **for the loop object, not the kernel body**
   (`:746-747`).
2. **FWD.** The default hypothesis **holds**, with E1 as the correction:

   ```
   method_for(A₁..Aₙ) <@> k
     ⇒  method_for(zip(A₁,ṫ₁), …, zip(Aₙ,ṫₙ)) <@> k̇
   k̇ = lambda(a₁, ȧ₁, …, aₙ, ȧₙ) -> Σᵢ ∂ᵢk(a₁..aₙ) · ȧᵢ
   ```

   Same S-dims (zip preserves rank, `formalism.md:131`), same iteration order,
   same output shape, same omp/BLAS eligibility in principle. Five places the
   pairing breaks or needs care:

   - **(a) Non-differentiable slots.** Int/index arrays have no tangent and must
     not be zipped; the loop then mixes zipped and bare slots. `classifyParam`
     (`Grad.fs:815`) is per-*parameter*; the C-track needs the same
     classification per *slot*.
   - **(b) Virtual arrays.** `range<I>`, `reverse<I>`, `blocked<I,K>`, `halo<…>`
     have `Void` element type and **erase completely** (`formalism.md:751`).
     Never zip them — this is E2 territory, and it is why halo (§2.9) is easy.
   - **(c) Identity detection.** The primal `method_for(A, A)` has one identity
     group. The tangent's two slots are two *occurrences of an expression*,
     `zip(A,Ȧ)`. Whether the compiler recognizes them as the same array is
     unproven. **Mitigation: bind once and reuse the name** —
     `let __z_A = zip(A, __t_A)` then `method_for(__z_A, __z_A)`. Let-bound zips
     are pinned (`loops/054:5`, and `examples/03_signal_conditioning.blade:139`
     binds a whole zipped loop `let Z = method_for(zip(r0,r1,r2))`).
   - **(d) Rank ≥ 1 kernels.** For a `T^1`-slice parameter, `∂ᵢk` is not a scalar
     partial and `Σ ∂ᵢk·ȧᵢ` is not a term-by-term rewrite; the kernel body has to
     be differentiated as a whole program. **Restrict tranche 1 to rank-0
     (scalar-slice) kernels** — which is also where rank-0 convergence makes
     `object_for(f) <@> (A,B) ≡ method_for(A,B) <@> f` (`formalism.md:1059-1063`),
     so one rule covers both constructors.
   - **(e) Kernel body admissibility.** The body must be in the AD-able fragment;
     recurse into the existing `walkExpr` (`Grad.fs:280`).
3. **REV.** map ↔ map holds for the **single-slot** case: the cotangent
   contribution is another pointwise map,
   `method_for(zip(A, cot)) <@> lambda(a, c) -> c * ∂k(a)`, accumulated into
   `__g_A`. For **n ≥ 2 slots** the loop is an outer product and the adjoint of
   slot i is a *contraction over the other slots' axes*:
   `__g_A(i) += Σⱼ cot(i,j)·∂₁k(A(i),B(j))`. That is a partial fold — and grad
   explicitly refuses `axes = n` (`Grad.fs:566`, *"reduce with an explicit
   `axes = n` is not differentiable (v1)"*). → **DESIGN** (needs the partial-fold
   adjoint) or Tier-1.
   Reverse needs the primal slice values at each cell; for a map over *parameter*
   arrays those are live, so the no-tape stance costs nothing. It costs when the
   map's input is itself a deferred intermediate (a pipeline stage) — see §2.10.
4. **Machinery.** Per-slot classifier; zip-let emitter; a **kernel-body
   differentiator returning an `Expr`** (grad's `adjointOf`, `Grad.fs:1148`,
   emits `NStmt`s into buffers — forward needs a pure `tangentExpr : Expr → Expr`
   over the same fragment; `derivRule` at `Grad.fs:147` is already mode-agnostic);
   where-clause propagation (§3.4).
5. FWD single-slot **MECHANICAL**; FWD multi-slot rank-0 **MECHANICAL** (+ §1
   DESIGN for symmetry); REV single-slot **MECHANICAL**; REV multi-slot
   **DESIGN**.
6. **Storage.** Per §1: dense tangent in tranche 1; compact only after ask #1.

### 2.6 `zip`

1. **Semantics.** `zip(A,B)(i..) = Tuple(A(i..), B(i..))` over the shared
   min-rank prefix; output symmetry = **intersection where all inputs agree**;
   the kernel gets one flat parameter per array by default, or the whole n-tuple
   under `Tuple<n>` (`formalism.md:131`).
2. **FWD.** `zip(A₁..Aₙ) ⇒ zip(A₁, ṫ₁, …, Aₙ, ṫₙ)` — a **flat interleave**, with
   the kernel's parameter list interleaved the same way. Not
   `zip(zip(A,Ȧ), …)`: nested tuples are one-level *data* nodes
   (`tuples/012:1-6`), and a `Tuple<N>` annotation lowers to N fresh inference
   variables nothing writes into, so element slots default to `double`
   (`tuples/012:14-24`, the KNOWN LIMIT). The flat interleave is the only
   spelling that types for non-`Float64` elements. Three-way and wider zips are
   pinned (`loops/052`), so arity is not a limit.
3. **REV.** A zip is a view; the adjoint splits the cotangent tuple back to its
   legs, which are just separate arrays. Trivial.
4. **Machinery.** Interleave rule + parameter-list rewrite. Watch
   `diagnostics/058:3` — a **one**-parameter kernel over a zip must be annotated
   `Tuple<2>`; doubling a 1-param kernel to 2 params sidesteps that, but a
   1-param kernel over a 2-zip becomes a 1-param kernel over a 4-zip and the
   annotation width must track.
5. **FREE–MECHANICAL.**
6. **Storage.** The intersection rule means interleaving a differently-symmetric
   tangent would collapse the class to `SymNone`. It does not: the jvp ABI gives
   the tangent the primal's declared `TypeExpr` **verbatim**
   (`plan-forward-mode-ad.md:55-56`), so the intersection is preserved. Worth an
   explicit pin.

### 2.7 `<&>` / `<&!>` — map form

1. **Semantics.** `<&>` fuses isomorphic loop *prefixes* then splits; `<&!>`
   demands full fusion and is **restricted to computations from the same
   MethodLoop**, because an ObjectLoop fixes S-dims only at application so
   structural identity cannot be verified (`formalism.md:1004-1008`;
   `TypeCheck.fs:8615-8620`).
2. **FWD.** Map legs are **independent** — no cross-leg coupling is needed, so
   this is one of the clean cases:
   `(L <@> k₁) <&!> (L <@> k₂) ⇒ (L̇ <@> k̇₁) <&!> (L̇ <@> k̇₂)`.
   The same-MethodLoop restriction makes the emitter's obligation explicit:
   **bind `let L̇ = method_for(zip(…))` once and reuse the name**; synthesizing
   the zip twice risks failing structural identity and hitting the `<&!>` refusal.
   Bonus, and it should be the shipped ABI at computation level: primal and
   tangent can be **two legs of one fused sweep** —
   `(L̇ <@> k) <&!> (L̇ <@> k̇)` — one traversal for value *and* derivative, the
   forward-mode win reverse mode cannot have.
3. **REV.** A parallel/fusion node produces a tuple; the adjoint splits the
   output cotangent per leg and sums the per-leg input cotangents — again `<&!>`
   over the shared loop. **MECHANICAL.**
4. **Machinery.** The shared-loop let emitter. Nothing else.
5. FWD **FREE–MECHANICAL**; REV **MECHANICAL**.
6. **Storage.** `σ(C₁ <&> C₂) = σ(C₁) × σ(C₂)` (`formalism.md:1096`) — inherited
   componentwise; the tangent component carries whatever §1 leaves it.

### 2.8 `<&!>` — reduction-join form (where the hypothesis breaks)

1. **Semantics.** `<&!>` also joins **reductions**: each leg normalizes to a
   `(traversal, fold, seed)` triple, the traversals fuse into one nest, and the
   legs **accumulate side by side** — *"a join is the shared-fold terminal
   generalized to a fold PER leg"* (`formalism.md:1010-1023`;
   `TypeCheck.fs:6030-6053`). The kernel and seed slots carry an `IRTuple` of k
   kernels / k seeds (`TypeCheck.fs:6049-6053`).
2. **FWD.** **This is the definitive break in the default hypothesis**, and
   `plan-forward-mode-ad.md:98-101` already records it. For a fold with kernel g,
   the tangent recurrence is

   ```
   ṫacc  =  ∂₁g(acc, x) · ṫacc  +  ∂₂g(acc, x) · ẋ
   ```

   which reads the **other leg's accumulator** `acc`. The join encoding gives
   each leg its own kernel and seed with **no channel from leg j to leg i**
   (`TypeCheck.fs:6049-6053`) — so the paired-leg spelling is not merely
   unimplemented, it is **not expressible**. → the Tier-1 lockstep lane, exactly
   as the plan says.

   **The carve-out that makes this rung worth building: additive folds.** For
   `g = (+)`, `∂₁g = ∂₂g = 1`, the coupling term vanishes, and

   ```
   reduce(c, (+))  ⇒  reduce( (L̇ <@> k) <&!> (L̇ <@> k̇), (+) )
   ```

   computes value and tangent in **one traversal**, at Tier-2. Additive is also
   the *only* fold grad supports today (`Grad.fs:571-572`), so this rule covers
   the entire existing differentiable reduce surface while strictly improving on
   its emission. The same carve-out extends to any **linear** fold kernel
   (`(+)`, `(-)`, scaling). `max`/`min` are piecewise-linear *selections* whose
   tangent must follow the argmax — a genuinely coupled 2-state fold → Tier-1.
3. **REV.** The adjoint of an additive reduce is a **broadcast** of the scalar
   cotangent over the traversal — the cleanest combinator-level pair in the whole
   algebra (`reduce ↔ broadcast`). grad does it in the lane today
   (`Grad.fs:595-598`); Tier-2 is a straightforward lift.
   For a **non-additive** fold, reverse needs the per-step primal accumulator
   *sequence* — a genuine tape, which the no-tape stance forbids; recompute is
   O(n²) in the fold length. → **BLOCKED**, and say so rather than shipping an
   O(n²) surprise.
4. **Machinery.** The additive-fold detector already exists
   (`Grad.fs:570-572`); the new parts are emitting a join instead of the lane,
   and getting the tangent leg's **seed** right (0.0, not the primal's `init`).
5. FWD additive **MECHANICAL** (highest value in the C-track); FWD general
   **Tier-1**; REV additive **MECHANICAL**; REV general **BLOCKED**.
6. **Storage / license.** A bare `omp` on a reduce kernel licenses fold
   reordering and *requires* commutativity (BL4016). `(+)` is commutative so the
   tangent leg inherits it. For a general g, **the derived tangent fold's
   commutativity and associativity are NOT implied by the primal's** — the pair
   state `(acc, ṫacc)` with a nonlinear step is generally non-associative even
   when g is. **Rule: never propagate a reduce kernel's `omp` onto a derived
   non-additive tangent fold.** Pin the refusal.

### 2.9 `halo` — the easiest case (E2)

1. **Semantics.** `halo<I, [o..]>` is a stencil traversal transformer over I
   with signed ordinal offsets, center 0 (`Ast.fs:447`); the kernel receives a
   **window** `w` and reads captured arrays at `A(w(o))`; the interior shrinks by
   the offset span (`loops/074:2-7`: extent 5, offsets [-1,0,1] → 3 cells).
   Extent agreement is guarded by BL3016 at typecheck with a BL8009 runtime twin,
   and the window is scope-restricted to `w(o)` form.
2. **FWD.** **No pairing at all.** Differentiate the body with respect to its
   captured-array reads, replacing each read `A(w(o))` by its partial times
   `__t_A(w(o))`:

   ```blade
   lambda(w) -> A(w(-1)) + A(w(0)) + A(w(1))
     ⇒ lambda(w) -> __t_A(w(-1)) + __t_A(w(0)) + __t_A(w(1))
   ```

   Same loop object, same halo, same shrunk output, same extent guard. The
   `w(o)`-only scope rule is preserved automatically because the derived body
   reuses the identical `w(o)` subterms — it never does arithmetic on `w`.
3. **REV.** The classic transposed stencil with flipped offsets,
   `ḡA(j) += Σ_o c_o · cot(j − o)`. But the adjoint's output domain (**full** I)
   is larger than the cotangent's domain (the **shrunk** interior), so it needs a
   zero-**Pad** boundary. `ExprHalo` carries only `inner: TypeExpr * offsets`
   (`Ast.fs:447`) — Shrink only. The forms that do carry
   `boundary ∈ Shrink/Pad/Periodic/Reflect` are `align`/`stencil`
   (`formalism.md:132-133`), which have an AST node (`Ast.fs:450`), a Lowering
   arm (`Lowering.fs:881`) and a TypeCheck arm (`TypeCheck.fs:5844`) but **zero
   corpus or example usage** — a paper surface. → no Tier-2 spelling for the
   transposed stencil today; **Tier-1 scatter-add**, which is exactly the
   `a(i) += e` shape grad's lane already handles.
4. **Machinery.** FWD needs only a captured-array-read arm in the body
   differentiator (grad already has the reverse twin at `Grad.fs:1154-1157`).
   REV needs a `halo` boundary parameter, or `align` promoted from paper.
5. FWD **MECHANICAL** — best value-per-line in the C-track. REV **DESIGN** /
   Tier-1 today.
6. **Storage.** Nothing to inherit: symmetry lives in the index type, not in the
   window; a single-window kernel declares no comm.

### 2.10 `>>@` / `@>>` — pipelines

1. **Semantics.** `>>@ : ObjectLoop × ObjectLoop → ObjectLoop` composes kernels
   then applies; `@>> : Computation × Computation → Computation` applies then
   composes over the same MethodLoop; both associative with `object_for(id)` as
   identity, and the Compose-Apply duality is proved (`formalism.md:1047-1057`,
   *"the mechanized proof is literally map fusion"*).
2. **FWD.** The chain rule **does** close at the pipeline level, and closing it
   is exactly the "re-differentiability" the retired quote said was missing. Make
   each stage a **pair→pair** kernel:

   ```blade
   ḟ = lambda(p: Tuple<2>) -> (f(p[0]), fprime(p[0]) * p[1])
   (object_for(f) >>@ object_for(g))  ⇒  (object_for(ḟ) >>@ object_for(ġ))
   applied to zip(A, __t_A)
   ```

   Tuple-valued kernels are real and pinned:
   `method_for(xs) <@> lambda(x) -> (x, x * 10.0)` (`loops/070:8`). Pair-in /
   pair-out means the rule is closed over its own output, so chains of any length
   work and jvp∘jvp composes.

   **The blocker is not the algebra, it is `Tuple<N>` element-type inference.**
   A width-only annotation lowers to N fresh inference variables nothing writes
   into, so slots default to `double` (`tuples/012:14-24`, `tuples/002`). For a
   dimensionless `Float64` rank-0 pipeline that default is *correct* and the
   encoding works today; for units, `Float32`, complex, or array-valued stages it
   silently mistypes. → tranche scope: dimensionless `Float64` rank-0 pipelines,
   with an explicit refusal otherwise.

   **The fallback that always works: fuse then differentiate.** Collapse
   `object_for(f) >>@ object_for(g)` into a single kernel `lambda(x) -> g(f(x))`
   and apply §2.5. Same answer, no new machinery, loses only the staging. This
   should be the *shipped* behavior; the staged rule is an optimization, not a
   capability.
3. **REV.** The adjoint reverses the composition, `adj(g∘f) = adj(f) ∘ adj(g)`,
   and each adjoint stage needs its own stage's **primal input**. Threading the
   primal forward as a pair and then running the reversed pipeline means
   materializing the intermediate array per stage — a tape by another name;
   recomputing the prefix per stage is O(depth²). Meanwhile grad already inlines
   same-module callees (`Grad.fs:46`) and would produce the correct straight-line
   adjoint for free once the pipeline is fused. → **do not build a reverse
   pipeline rule; fuse and inline.**
4. **Machinery.** Tuple-state kernels; `Tuple<N>` element-type inference
   (compiler ask #2); a stage-kernel differentiator.
   Note `<$>` inherits a pre-existing hole: CodeGen refuses `<$>` over a `<|:>`
   fallback (`CodeGen.fs:15388-15393`).
5. FWD **DESIGN** (blocked below `Float64`); REV **skip — inline**.
6. **Storage.** Pipeline kernels are rank-0 (rank-0 convergence,
   `formalism.md:1059-1063`); no comm to inherit.

### 2.11 `guard`

1. **Semantics.** `guard(p, c)` = c if p, else **zeros of c's shape**;
   `guard(p, guard(q,c)) ≡ guard(p && q, c)` (`formalism.md:1072`). The
   interpreter folds the predicate into the kernel body as `cond ? body : 0`
   (`Interp/Loops.fs:464-467`) for apply bodies, and evaluates it once for
   concrete bodies since *"the predicate is a scalar here (it cannot reference
   per-cell values without a kernel)"* (`:472-478`).
2. **FWD.** `guard(p, c) ⇒ guard(p, ċ)` — the **same** predicate, undifferentiated
   and reused by name. Sound wherever p is independent of differentiable data,
   which is the idiomatic case (`examples/03_signal_conditioning.blade:104`,
   `let ch2_enabled = false`). Where p does depend on differentiable data, the
   Dirac term at the switching set is dropped — the standard subgradient
   convention, which the jvp plan has already committed to for if/match with a
   *pinned* convention (`plan-forward-mode-ad.md:158-161`, the ml oracle's
   zero-norm convention, `src/ml/Autodiff.fs:103`). Admit, document, pin.
3. **REV.** `guard(p, cot)` routed into c's adjoint — the same predicate again.
   Pleasingly symmetric.
4. **Machinery.** Essentially none; guard is a wrapper, recurse into the body.
5. **FREE** both directions, given the convention.
6. **Storage.** `shape(M <@> zero) = S-dims(M)` and `σ(M <@> zero) = σ(M)`
   (`formalism.md:1090`); guard preserves shape and symmetry, so the tangent
   inherits whatever the body's rule produced.

**Why guard is the right refuge for `<|>` users** (§2.13): guard's predicate is
*explicit* and its false branch is a *genuine zero*, so the discontinuity is a
boundary the user chose and can reason about, not one manufactured by a value
test on the data.

### 2.12 `mask` / `compound`

1. **Semantics.** `WHERE p` ≡ `compound(A, mask(A, p))`; masks compose with
   positional Bool `&&` (`docs/features/sql.md:23-24`); `compound(A, m)`
   materializes a masked view, and an inline `compound(A, mask(A,p))`
   auto-materializes the mask first (`sql.md:60-73`). `CompoundIdx` identity is
   the **whole-mask hash** with contiguous lex-order storage over mask-true
   tuples (`formalism.md:359-364`).
2. **FWD.** The mask is a predicate on the **primal**, hence a constant of the
   tangent program: `compound(A, m) ⇒ compound(__t_A, m)`, reusing the **same
   mask binding by name**. Recomputing `mask(__t_A, p)` would be catastrophically
   wrong — the tangent's values have nothing to do with the primal's predicate,
   so the two views would not even have the same cardinality. Given the mask,
   `compound` is a pure gather: **linear**. Switching-set Diracs are dropped, as
   in §2.11.
3. **REV.** `gather ↔ scatter`, and — the pleasant surprise — **both halves are
   spellable**. The scatter back from compact to dense *is* the compound-left
   `<|:>`: `S <|:> D` yields a dense array shaped like D with S overlaid on the
   present cells (`Interp/Loops.fs:437-444`, `Interp/ArrayOps.fs:1456`;
   `TypeCheck.fs:8689-8693`). So

   ```
   adjoint of compound(A, m)  =  cot <|:> zeros(shape A)
   ```

   **`<|:>` is `compound`'s transpose.** Tier-2, today.
4. **Machinery.** The emitter must **hoist an inline mask to a named `let`**
   before it can be shared between primal and tangent (`sql.md:73`). Runtime
   extents: `extents(compound(A,m))` is a runtime value (`sql.md:33`), and grad's
   extent env is static-only (refusal at `Grad.fs:599`) — new plumbing.
5. FWD **MECHANICAL**; REV **MECHANICAL**.
6. **Storage.** The tangent's compound type is *identical* to the primal's (same
   whole-mask hash ⇒ O(1) type equality, `formalism.md:359-360`), so the two
   co-iterate by construction.

### 2.13 `<|>` — value branching: **BLOCKED, and the recommendation is SKIP**

1. **Semantics.** `c₁ <|> c₂` = **first non-zero**; associative, idempotent,
   `M <@> zero` is the identity (`formalism.md:1074`). Executably: materialize
   both sides, elementwise `(lhs != 0) ? lhs : rhs`, scalars by the same rule
   (`Interp/Loops.fs:398-407`, `choiceArray` `:409-420`). The distinguishing
   sentence: *"an allocated zero survives fallback but not choice"*
   (`TypeCheck.fs:8687-8688`).
2. **FWD.** The a.e. derivative is `ẋ_out(i) = lhs(i) ≠ 0 ? l̇hs(i) : ṙhs(i)`.
   Three problems, in increasing severity:

   **(a) The naive pairing is silently unsound.** `l̇hs <|> ṙhs` branches on the
   **tangent's** zero test. A cell with `lhs(i) ≠ 0` but `l̇hs(i) = 0` — utterly
   ordinary — takes the *rhs* tangent. Wrong answer, no diagnostic. So the rule
   can never be "the same combinator over tangents", which is the entire shape of
   the C-track. It must be a select driven by the **primal**, and the only
   elementwise value-select in the algebra is `<|>` itself, which selects on the
   wrong operand. Recovering the primal predicate per cell requires a kernel over
   `zip(lhs, l̇hs, ṙhs)` — at which point you have abandoned the combinator,
   written a map, changed the iteration structure, and forfeited `<|>`'s property
   that the rhs need not be materialized.

   **(b) The function is DISCONTINUOUS, not merely non-smooth.** As `lhs(i) → 0⁻`
   the output → 0⁻; **at** `lhs(i) = 0` the output is `rhs(i)`, generally nonzero.
   That is a jump of size `rhs(i)` on the switching set. This is categorically
   worse than `abs`/`relu`/`max`, where the subgradient convention is defensible
   *because the function is continuous*. Here the "derivative" is not a
   subgradient of anything — it is one branch's derivative, reported silently.
   Every finite-difference check disagrees near the switching set and no tolerance
   fixes it.

   **(c) The pathological set is the design point.** `<|>` is the *failover*
   idiom, and its inputs are engineered to hit zero:
   `examples/03_signal_conditioning.blade:99-103` — dead channel is all zeros,
   *every* element falls through. The measure-zero argument that rescues `abs`
   fails empirically here.
3. **REV.** All three problems, plus the branch predicate must be recovered in
   the reverse sweep (recomputable, so that part is the easy part).
4. **Machinery.** Irrelevant — no amount of machinery fixes (b).
5. **BLOCKED — SKIP, with this justification recorded.** Concretely: keep the
   refusal (F0.4's arm already covers `OpChoice`, `Grad.fs:288-294`) and upgrade
   the message to name the discontinuity and point at `guard` (§2.11) or a smooth
   blend. **Do not ship an a.e. rule**: for this operator it is a silent-wrong-
   answer generator of the same class as the unknown-named-call zero that F0.3
   exists to kill.
   *Escape valve if ever demanded*: require a user assertion that `lhs(i) ≠ 0`
   everywhere. Under that hypothesis `A <|> B ≡ A`, the derivative is `Ȧ`, and
   the rule is trivially correct. It is a real, checkable special case and costs
   nothing to state.
6. n/a.

### 2.14 `<|:>` — storage branching: **the retired quote's grouping is wrong; this one is FREE**

1. **Semantics.** `A <|:> B` reads `A(i)` **if allocated**, else `B(i)`, checked
   per curry level; A's layout dominates iteration order; symmetric A requires
   symmetric allocation (`formalism.md:139`). Storage-keyed, not value-keyed —
   *"an allocated zero survives fallback but not choice"*
   (`TypeCheck.fs:8686-8688`). Two regimes: **dense-left**, where A is fully
   allocated so `A <|:> B = A` and B is **not even forced**
   (`Interp/Loops.fs:427-436`), and **compound-left**, the SQL sparse overlay
   (`:437-444`). Sparse-left is refused (`TypeCheck.fs:8745`).
2. **FWD.** The branch predicate is **allocation** — a property of the storage /
   index type, not of any value — hence a constant of the differentiable program.
   `<|:>` is therefore **linear in its operands** and the default hypothesis holds
   exactly:

   ```
   d(A <|:> B)  =  Ȧ <|:> Ḃ,   provided Ȧ carries A's allocation
   ```

   And it does, for free: the jvp ABI gives the tangent the primal's declared
   `TypeExpr` **verbatim** (`plan-forward-mode-ad.md:55-56`), so a
   `CompoundIdx<m>`-typed A yields a `CompoundIdx<m>`-typed Ȧ with the identical
   whole-mask-hash identity (`formalism.md:359-360`). Dense-left degenerates to
   `Ȧ`, mirroring the primal's `A`.
3. **REV.** The transpose of a storage-keyed selection is a storage-keyed
   **split**. Dense-left: `__g_A += cot`, `__g_B` untouched — B contributes
   nothing because it is never read (`Interp/Loops.fs:427-429`). Compound-left:
   `__g_A += compound(cot, m)` and `__g_B += ` the complement.
   *Caveat:* the complement spelling `compound(cot, !m)` relies on `!` over a
   Bool mask array. `&&` over masks is pinned (`bracketed/011`) and `!` is in the
   surface (`formalism.md:1465`), but **no corpus test pins mask negation** —
   probe before relying on it.
4. **Machinery.** None for dense-left; mask-complement hoisting for
   compound-left.
5. FWD **FREE** (dense-left) / **MECHANICAL** (compound-left); REV
   **MECHANICAL**.
6. **Storage.** *"symmetric A requires symmetric allocation"* (`formalism.md:139`)
   is satisfied by the verbatim tangent type by construction. The sparse-left
   refusal applies identically to the tangent, so no new refusal surface appears.

> **Verdict on the retired quote.** It names `<|>` and `<|:>` together as "the
> exceptions" and draws the right distinction — *value*-branching vs
> *storage*-branching. But the distinction is not a shared difficulty, it is the
> reason the two verdicts **diverge maximally**: branching on a value makes the
> operator a discontinuous function of the differentiable data (BLOCKED, §2.13);
> branching on storage makes it a *linear* operator with a constant selector
> (FREE). `<|:>` belongs in the **first** tranche, not the exception list — and
> it is `compound`'s missing transpose (§2.12), which makes it load-bearing for
> reverse mode too.

### 2.15 `reynolds`

1. **Semantics.** The value-level symmetrizing wrapper
   `K(x₁..xₙ) = Σ_σ [sign] g(x_σ(1)..x_σ(n))`, `positions=[…]` restricting to a
   subset (`formalism.md:613-616`). Terms are deduplicated into
   **integer-coefficient** representatives in first-occurrence order
   (`ReynoldsCore.fs:49-79`); antisymmetric Reynolds accumulates **net sign** and
   drops terms that cancel (`:68-71`). The `comm` license is *declared*, not
   derived — *"an interchangeable-for-iteration declaration, not a truth claim
   about g"*, and self-licensing is an open design question
   (`formalism.md:624-628`). Identical arrays ⇒ full transfer; distinct arrays ⇒
   **dense** output (`:619-635`).
2. **FWD.** `d(reynolds g) = reynolds(dg)` is **TRUE — but only under the joint
   permutation action**: the seeds must permute *with* their values,

   ```
   K̇(x, ẋ)  =  Σ_σ c_σ · ġ( x_σ(1), ẋ_σ(1), …, x_σ(n), ẋ_σ(n) )
   ```

   The proof is one line: reynolds is a fixed finite integer-coefficient linear
   combination of evaluations of g (`ReynoldsCore.fs:49-79`) and differentiation
   is linear, so it commutes with the sum; the permutation acts on argument
   **slots**, and under E1 each slot carries a `(value, tangent)` pair.
   Wrapping `reynolds` around the *flat 2n-parameter* tangent kernel with an S₂ₙ
   or S_n × S_n action is **unsound** — the same failure mode as
   `per_dim_swap_not_symmetry`, one level up. So this is a second, independent
   argument that E1's zip-pairing is not a convenience: **one zipped slot = one
   permutable unit** is what makes the reynolds rule statable at all.

   Two wrinkles that will bite silently:

   **(a) Recompute the term plan on the tangent body; never reuse the primal's.**
   `reynoldsTermPlan` groups permutations by a canonical key built from the
   *rendered kernel body* (`ReynoldsCore.fs:44-47`, `:56-64`). The tangent body is
   a different expression, so its dedup classes can be strictly finer. Reusing the
   primal's coefficients multiplies the wrong terms. The fix is free — call
   `reynoldsTermPlan` on the derived kernel — but the bug is invisible.

   **(b) Antisymmetric cancellation differs between primal and tangent.** A term
   set whose net sign cancels to 0 for g need not cancel for ġ, and vice versa
   (`ReynoldsCore.fs:68-71`). Correct as long as (a) is honored; wrong in a way no
   FD check on a symmetric input will catch if it is not.
3. **REV.** Same linearity: the adjoint of a Reynolds sum is the Reynolds sum of
   the adjoints, with the cotangent routed to each permuted slot and
   **accumulated** — each input appears in up to n! slots, so the accumulation
   multiplicity is real. That is exactly the setting of BladeJacobian's
   `symmetric_accumulation` (off-diagonal ×2, diagonal ×1 at r=2;
   `docs/proofs.md:606-614`). The `+=` in the lane already implements the
   accumulation; the risk is buffer aliasing across permuted slots.
4. **Machinery.** Joint-permutation-aware kernel synthesis; plan recomputation
   (free). No new IR.
5. FWD **MECHANICAL** given §2.5; REV **DESIGN**.
6. **Storage.** Inherits the H ∩ Stab dichotomy verbatim (`formalism.md:619-635`):
   identical arrays ⇒ transfer (subject to §1), distinct arrays ⇒ dense. Since E1
   preserves array identity exactly when the primal had it, the dichotomy carries
   over unchanged. Note `reynolds` does not self-license `comm`, so the tangent
   inherits only what was *declared*.

### 2.16 `gram` — the best reverse rule in the C-track

1. **Semantics.** `gram(A, B) = A·Bᴴ`, i.e. `G(i,j) = Σₖ A(i,k)·conj(B(j,k))`;
   square + Hermitian/symmetric when A and B are the same array, dense otherwise
   (`Ast.fs:489`). `gram` is a **keyword, not an identifier**. Order-2 PPL formers
   emit it precisely to inherit BLAS routing.
2. **FWD.** Bilinear, so the rule is the product rule at array level:
   `gram(Ȧ, B) + gram(A, Ḃ)`; for the self case
   `gram(A,A) ⇒ gram(Ȧ,A) + gram(A,Ȧ)`, which is **symmetric by construction**.
   That is a symmetry the tangent gets from the *bilinear structure*, not from a
   kernel annotation — so it survives §1's dense-tangent restriction and does not
   wait on ask #1.
3. **REV.** Real case: `__g_A += cot · B`, `__g_B += cotᵀ · A`; for the self case
   `__g_A += (cot + cotᵀ)·A` (the multiplicity again). Both adjoints are
   **matmuls**, so both inherit BLAS routing. This is the one place where a
   Tier-2 reverse rule is unambiguously better than the lane.
4. **Machinery.** A `matmul`/`gram` adjoint emitter; transpose of the cotangent
   (native, `Ast.fs:487`).
5. FWD **MECHANICAL**; REV **MECHANICAL** — and high value.
6. **Storage.** Self-gram tangent symmetry is structural, per §2.16.2.

### 2.17 `group_by` / `group_keys` / `sort`

1. **Semantics.** `group_keys(k₁..)` builds a CSR grouping structure;
   `group_by(values, gk) : Array<T like I> × GroupKeys<I> → Array<T like
   GroupOuter, GroupMember>` is a ragged grouped view
   (`docs/features/sql.md:185-237`). Crucially: arrays partitioned by **the same
   `group_keys` binding co-iterate** (`sql.md:256-261`).
2. **FWD.** `gk` is built from *keys* — non-differentiable data — so it is a
   constant of the tangent program: `group_by(v, gk) ⇒ group_by(v̇, gk)`, reusing
   the `gk` **binding by name**. `sql.md:256-261` documents exactly the property
   this needs (same-`gk` co-iteration), so primal and tangent grouped views
   co-iterate by construction. Linear gather.
   `sort(A, key)`: the tangent must apply the *same* permutation. There is no
   "sort by another array's order" form; the plausible route is sorting
   `zip(A, Ȧ)` under a key reading only the value half, which is **unverified**
   (no corpus pin) → DESIGN.
3. **REV.** The adjoint is a scatter back through the grouping, and there is **no
   ungroup surface** and no `<|:>`-analogue for `RaggedIdx`. → **BLOCKED at
   Tier-2**; Tier-1, with ragged storage in the lane as the real difficulty.
4. **Machinery.** `gk` hoisting to a named binding; ragged lane support.
5. FWD `group_by` **MECHANICAL**, `sort` **DESIGN**; REV **BLOCKED at Tier-2**.
6. **Storage.** RaggedIdx; no comm.

### 2.18 `>>=` and `<$>`

1. **Semantics.** `>>= : Computation α × (α → Computation β) → Computation β`,
   monad laws hold; `f <$> c ≡ c >>= pure ∘ f` (`formalism.md:990-992`). The
   interpreter forces the computation, binds the continuation's parameter to the
   value, and forces the body (`Interp/Loops.fs:515-525`).
2. **FWD.** `>>=` is **a `let` in disguise**: materialize, bind, continue. The
   jvp let-walker handles it directly if it can see through the continuation
   lambda to differentiate the body — which is the same capability §2.5 needs.
   `<$>` is a post-map on a computation and needs pair threading, i.e. §2.10's
   `Tuple<N>` problem, or fusion into the producing kernel.
3. **REV.** Skip — inline (§2.10.3).
4. **Machinery.** Continuation-lambda body differentiation.
5. `>>=` **MECHANICAL–DESIGN**; `<$>` **DESIGN**. Right-distribution **fails**
   for this monad (`formalism.md:1083-1087`) — any rewrite that reassociates a
   bind chain must not assume it.
6. n/a.

### 2.19 Rank-polymorphic / `Poly<T^k>` pack kernels — **BLOCKED by architecture**

1. **Semantics.** Arity polymorphism varies the *number* of inputs, and the
   arity determines output rank, loop depth, and symmetry
   (`formalism.md:870-885`); `moment <@> (data, data)` is covariance,
   `<@> (data,data,data)` is coskewness. Arity is resolved by the **loop-object
   judgment at the apply site**, not at the declaration.
2/3. **FWD and REV.** The tangent kernel's parameter list is `2n` where n is the
   pack arity. The transform runs **before typecheck** (`Grad.fs:22-25`), so at
   transform time n is unknown and the parameter list is not constructible. This
   is not a missing rule — it is the pipeline slot. Fixing it means deferring
   tangent synthesis until after arity deduction, i.e. moving the transform out of
   its slot: a different architecture.
4. **Machinery.** Post-deduction synthesis (out of scope).
5. **BLOCKED — record and stop.** `reynolds` over a `Poly` pack inherits the same
   blockage (`formalism.md:776`, the poly `for args in SymIdx<arity(args), N>`
   form).
6. n/a.

### 2.20 Recursive arrays and `let rec` (for completeness)

Covered by the base plan, not the C-track: F1 uses the `StmtForIn` lane and F2
lifts the additive-only restriction (`plan-forward-mode-ad.md:93-97`,
`:157-161`). The C-track's only interaction is that a recursive array's slice
expression may *contain* combinators, in which case the C-track rules apply
inside the lane body. The **implicit zero history** rule
(`formalism.md:823-843`) transfers verbatim to the tangent: a tangent read that
runs off the start of the prefix is zero, which is also the correct tangent of a
zero primal read. No new rule needed — but it is worth a pin, because it is the
kind of thing an implementation "helpfully" guards.

---

## 3. Machinery inventory — what Grad.fs / a Jvp module lacks

1. **A kernel-body differentiator returning an `Expr`.** `adjointOf`
   (`Grad.fs:1148`) accumulates `NStmt`s into cotangent buffers. Forward needs a
   pure `tangentExpr : Expr → Expr` over the same fragment, so a derived *lambda
   body* can be synthesized. `derivRule` (`Grad.fs:147`) is already
   mode-agnostic (returns d/du as an `Expr` of u) and is directly reusable; so is
   the `zeroDerivIntrinsics`-vs-`None` split (`Grad.fs:140`, `:147`) that keeps
   `digamma` an honest refusal rather than a silent zero.
2. **Per-slot classification.** `classifyParam` (`Grad.fs:815`) classifies
   parameters. Loop slots need the same call, plus two new verdicts: *virtual
   array — never zip* (`formalism.md:751`) and *Int/index array — never zip*.
3. **The zip-let emitter with name reuse.** Structural identity for `<&!>`
   (`TypeCheck.fs:8615-8620`) and for `method_for(A,A)` identity groups depends on
   the *same binding* appearing in both positions, not on two structurally equal
   expressions. Emit `let __z_A = zip(A, __t_A)` once.
4. **Where-clause propagation onto derived kernels.** `ExprLambda` carries
   `whereClause: WhereClause option` (`Ast.fs:426`) with `Commutativity`,
   `Antisymmetry`, `Parallel`, `TDims`, and open `Custom` conjuncts
   (`Ast.fs:354-380`), so a derived kernel *can* carry metadata. Three hazards:
   - **(a)** `comm`/`anticomm` are **positional** ident lists and the derived
     kernel renumbers positions. `diagnostics/060:12-14` calls this out as
     **silent** — *"a misplaced comm group degrades to dense storage with no
     diagnostic."*
   - **(b)** Named-function kernels **eta-expand with no where-clause at all**,
     so a kernel written as `function k(a,b) where comm(a,b)` loses its metadata
     before the tangent transform can read it. Each attribute needs its own
     side-channel; this is a pre-existing seam the C-track inherits.
   - **(c)** The **parallel** strategies do survive the `Tuple<N>` expansion —
     they *"resolve BY NAME to the operand a parameter contributes"* and the
     licence is rewritten onto the synthesized row params, with `tuples/014` as
     the accept pin (`diagnostics/060:18-23`).

   **Rule:** propagate `omp`/`cuda`/`mpi`; **drop** `comm`/`anticomm` (accept a
   dense tangent) until ask #1 lands; and **never** propagate a reduce kernel's
   bare `omp` onto a derived non-additive tangent fold (§2.8.6).
5. **Tuple-state kernels.** Required by pipelines (§2.10) and by any coupled fold.
   Blocked below `Float64` by `Tuple<N>` element-type inference
   (`tuples/012:14-24`, `tuples/002`) — ask #2.
6. **Extent information.** grad threads a **static** extent env for reduce sources
   and refuses without one (`hoistReduces` `Grad.fs:555`, refusal `:599`). The C-track
   rules needing extents are: the multi-slot map adjoint (contraction axes),
   halo's shrink arithmetic, and `<|:>`'s complement mask. `extents(A)` is a
   surface form (`formalism.md:1454`) so runtime extents are reachable, but that
   is new plumbing on a static-only env.
7. **A no-tape story for reverse through deferred chains.** grad recomputes
   loop-body lets inside the adjoint loop (`Grad.fs:61-63`, `:1301`) and
   keeps function-level lets in scope. Combinators sharpen the question because
   they *encourage* deep deferred pipelines: "recompute" then means re-running
   earlier stages, O(depth²). This is the main reason §4's C6 recommends **not**
   building a general reverse combinator pass.
8. **Interpreter parity.** `test interp` / `diff-oracle` are the gate, and the
   interpreter's subset is narrower than codegen's:
   `Interp/Loops.fs:1790-1791` raises `InterpUnsupported` for *"reduce over
   symmetric/Reynolds/fused computation (M2.5)"*. A tangent program that fuses a
   reduce over a symmetric loop is **unrunnable in the interpreter** even though
   codegen handles it — and skips do not affect exit codes, so the differential
   coverage would vanish silently (`plan-forward-mode-ad.md:221-226`). **Design
   tranche 1 to stay inside the interpreter's subset, and require zero skips.**

---

## 4. The recommended C-track ladder

**C0 — prerequisites.** F0–F3 of `plan-forward-mode-ad.md` land. Ask #1 (§5) is
needed only for C5; ask #2 only for C7.

**C1 — the linear closure** (forward *and* reverse together; no kernel
differentiation at all).
`pure`, `|> compute`, `<*>`, `stack`, `sequence`, `replicate`, `join` (fwd),
`transpose`, `guard`, **`<|:>`**, `compound` (given a hoisted mask), `group_by`
(given a hoisted `gk`).
Every one is a reindexing or a wrapper: tangent = the same op on tangents;
adjoint = the transposed reindexing wherever a surface inverse exists.
*Why first:* no derivative rules, no kernel synthesis, no symmetry question —
pure plumbing that establishes the paired-binding walker everything else uses,
and it converts a large slice of today's blanket refusal into support.
*Deliverable:* the walker + a `tests/corpus/ad-jvp-comb/` linear block.

**C2 — the rank-0 map** (forward). `method_for`/`object_for`/`<@>`, `zip`,
`<&>`/`<&!>` map form, single- and multi-slot, rank-0 kernels, **dense tangent
output** (no comm propagation). Plus **the additive reduce as a joined leg**
(§2.8) — value and tangent in one traversal, which is the payoff case and covers
grad's entire existing reduce surface.
*Why second:* it is what everyone means by "AD through combinators", and it is
MECHANICAL once C1's walker exists.

**C3 — `halo`** (forward). E2: no pairing, near-free after C2's body
differentiator, and it unlocks stencil/PDE/ML surfaces. Best value per line in
the whole track.

**C4 — `reynolds`** (forward), with the joint-permutation rule and the
recompute-the-term-plan discipline (§2.15). Cheap given C2 — and the case where
getting it wrong is silent, so it should not be left to drift.

**C5 — symmetry inheritance** (forward). Gated on ask #1. Turns C2's dense
tangents compact and restores the r! saving on the tangent leg.
`tangent_joint_swap` is the specification; there is nothing to design.

**C6 — the reverse subset actually worth building.** Honest answer: **small.**
grad inlines everything (`Grad.fs:46`) and emits into a mut-buffer lane that
already handles gather/scatter correctly. Build Tier-2 adjoints only where the
surface inverse **exists** and the win is BLAS or storage:
- `gram` (§2.16) — both adjoints are matmuls, inherits BLAS. Do this one first.
- additive `reduce ↔ broadcast` (§2.8).
- `compound ↔ <|:>` (§2.12/§2.14) — gather/scatter, both spellable today.
- `stack` / `transpose` (§2.3/§2.4).
Everything else — `join` (no `subset`), `halo` (no `Pad`), `group_by` (no
ungroup), `decompact` (no `compact`) — has **no surface inverse**, and creating
one is a language change, not an AD change.
**Recommendation: do not build a general reverse-through-combinators pass.**
Extend grad's front end to *lower* the C1 linear closure into lane statements
(so grad stops refusing programs it could handle), add the four adjoints above,
and stop.

**C7 — pipelines** (`>>@`, `@>>`, `<$>`), forward only, dimensionless `Float64`
rank-0, gated on ask #2. Until then ship the **fuse-then-differentiate** fallback
(§2.10.2), which gives the identical answer with no new machinery. The staged
rule is an optimization, not a capability — do not let it gate the tranche.

**Skipped, with justification recorded:**
- **`<|>`** — discontinuous, and its pathological set is its design point
  (§2.13). Keep the refusal; improve the message.
- **`Poly<T^k>` / arity-polymorphic kernels** — the transform's pre-typecheck
  slot makes the tangent parameter list unconstructible (§2.19).
- **`sort`** — key-over-zip is unverified (§2.17).
- **`align` / `stencil`** — paper surface: AST + Lowering + TypeCheck arms, zero
  users (§2.9).
- **Complex / Wirtinger** — inherits `plan-forward-mode-ad.md:249`.
- **`>>=` beyond the let reading**, and right-distribution rewrites
  (`formalism.md:1083-1087`).

---

## 5. Compiler asks (outside AD, but the C-track's ceiling depends on them)

1. **Relax BL3999: give `comm(p, q)` on `Tuple<N>` kernel parameters the joint
   pair-swap meaning.** `diagnostics/060:15-16` refuses because *"there is
   nothing settled to remap TO"*; `proofs/BladeJacobian.v:365-398`
   (`tangent_joint_swap`) settles it, with `per_dim_swap_not_symmetry` and
   `semantic_hypothesis_insufficient` fencing the license to the structural
   judgment. Small, well-scoped, and it is the **only** route to symmetric
   tangent storage. Without it, C5 does not exist.
2. **`Tuple<N>` element-type inference** (`tuples/002`, `tuples/012:14-24`) —
   unblocks pipelines and every tuple-state kernel beyond bare `Float64`.
3. *(reverse only, optional)* **A boundary parameter on `halo`** — or land
   `align`/`stencil`, which already have AST (`Ast.fs:450`), Lowering
   (`Lowering.fs:881`) and TypeCheck (`TypeCheck.fs:5844`) arms and zero users.
   The transposed stencil needs `Pad`.
4. *(reverse only, optional)* **A `subset` / slice surface** — `join`'s inverse
   (`formalism.md:137` lists it; `Ast.fs` has no node).

---

## 6. Verification notes

- Reuse `plan-forward-mode-ad.md` §5 wholesale (tolerance-based pins,
  `tests/Expect.fs:560/645`; thresholded booleans for FD, never FD residuals; the
  reject-parity census model).
- **The C-track's own gate: combinator-vs-lane differential.** For every rule in
  §2, one corpus test computing the same tangent twice — once through the
  combinator rule, once through the Tier-1 lane (or through grad's basis sweep) —
  pinned `EXPECT: resid = 0`. This is the *only* check that catches a rule that is
  **plausible and wrong**, which is the failure mode of essentially every rule
  above (`<|>`'s tangent-keyed branch, reynolds' reused term plan, a misplaced
  comm group).
- Pin the `<&!>` shape explicitly: both legs must come from **one named loop** or
  the fusion typecheck refuses (`TypeCheck.fs:8615-8620`,
  `formalism.md:1007-1008`).
- **Interpreter subset:** do not emit reduce-over-symmetric/Reynolds/fused in
  tranche 1 (`Interp/Loops.fs:1790-1791`) or the diff gate disappears silently.
  Require `N/0/0` with **zero skipped**.
- **Reynolds:** pin an antisymmetric case where the *primal's* dedup cancels a
  term and the *tangent's* does not (§2.15 wrinkle (b)); a plan-reuse bug is
  otherwise invisible.
- **`<|:>`:** pin dense-left and compound-left separately — the two regimes have
  different adjoints, and dense-left never even forces the right operand
  (`Interp/Loops.fs:427-429`).
- **`zero`:** pin that the tangent of `zero` is `0.0` under both `+` and `*`
  recursion base cases (§2.3 trap).
- No `// WARN:` pins in the C-track corpus — they are strict in both directions
  and would force auditing every `import ad` file.
- New `.blade`/`.fs` files land LF-in-index (`git ls-files --eol` before commit);
  corpus files are byte-pinned assets.

---

## 7. Open questions to probe before building

1. Does `method_for(zip(A,Ȧ), zip(B,Ḃ)) <@> lambda(pa: Tuple<2>, pb: Tuple<2>)`
   give **two** loop slots (rank-2), as extrapolated from `tuples/011:22`? Nothing
   pins the two-zip case.
2. Does identity detection see `method_for(__z, __z)` — one let-bound zip used
   twice — as a single identity group (§2.5(c))? If not, C5 is dead even with
   ask #1.
3. Is `!m` over a Bool mask array spellable and lowered (§2.14.3)? No corpus pin
   exists.
4. Can `sort(zip(A,Ȧ), key)` take a key reading only the value half (§2.17)?
5. Does the `omp` licence rewrite (`diagnostics/060:18-23`, `tuples/014`) survive
   onto a kernel whose parameter count **doubled**, or is it position-sensitive in
   a way the doubling breaks?
