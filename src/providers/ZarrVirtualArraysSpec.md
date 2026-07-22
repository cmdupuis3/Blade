# Spec sketch: Zarr through Virtual Arrays (iteration sources, not lazy handles)

Status: DESIGN SKETCH, REWORKED 2026-07-15 against formalism §7.3. The first
draft of this document mis-framed provider variables as "virtual arrays whose
generator is store I/O" — that is NOT what a Blade virtual array is.

## What virtual arrays actually are (formalism §7.3)

Type-level **iteration sources** with `Void` element type that erase
completely at codegen: `range<I>` (enumerate I in storage/lex order),
`reverse<I>`, `blocked<I, K>` (K-sized cache blocks, spec level). They have
types *like* arrays but carry **no content** — they exist to map index types
in various orders, and they compose with real arrays in one loop:

```blade
method_for(range<I>, A, B) <@> lambda(i.., a, b) -> ...
```

A `range` may span **several** index types — `range<I, J, ...>` is one virtual
array over the PRODUCT, uncurried into nested loop levels, presenting one
(per-slot tagged) param per slot. `reverse` and `blocked` remain single-index.
The one exclusion is `CompoundIdx`, which is itself a whole iteration space and
so cannot share a `range<>` (see the non-goals below).

A provider variable is the opposite kind of thing: it is all content (on
disk) and no iteration policy. The correct division of labor is therefore:

> **Virtual arrays direct the iteration (domain and order); providers supply
> the content at the iterated coordinates.**

Everything below is that one sentence applied to the zarr format.

## The gap list

1. **Window iteration sources** (the `read_window` replacement).
   A virtual source enumerating a contiguous sub-interval of an index type:
   `range<I, lo, hi>` (or `window<I, lo, hi>`) — for a packed
   `I = SymIdx<r, n>`, it enumerates the canonical tuples with every
   coordinate in [lo, hi): the translated sub-simplex, C(w+r-1, r) tuples,
   in ascending-lex order. The typing rule is `read_window`'s rule relocated
   to the virtual layer (`SymIdx<r, hi-lo>`-shaped iteration); mixed
   per-component intervals are rejected (not closed over the packed family).
   Dense axes window freely. Erases to loop bounds — nothing else.
2. **Windowed co-iteration with provider inputs.** Composing a window source
   with a provider variable makes subsetting a LOOP property:

   ```blade
   let W = method_for(window<SymIdx<2, n>, 2, 6>, C) <@> lambda(i, j, c) -> c |> compute
   ```

   — where `C` is provider-backed. For a **streamed** input the fiber/cell
   reads already happen at loop-supplied coordinates, so the restricted
   domain restricts I/O for free; the provider's only new obligation is the
   chunk-touch query it already answers internally (window ∩ chunk-grid /
   tile-interval intersection — implemented in the read_window and blocks
   machinery). For a **materialized** input, forcing under a window source
   materializes only the window (today's read_window emitter, reused).
3. **Order-aware provider I/O.** This is where "virtual arrays could make
   streaming complex" bites: an iteration source fixes the VISIT ORDER, and
   streamed I/O cost depends on how that order meets the chunk grid.
   - `range<I>` (storage order): the streaming default; sequential chunks.
   - `blocked<I, K>` : the friendly reorder — visiting cells tile-by-tile is
     exactly chunk-aligned when K matches the store's chunk/tile extents
     (zarr site-chunks; simplex-blocks tiles). The P4 tile-blocked pair
     iteration of StreamingIONotes.md is precisely
     `blocked<SymIdx<2, M>, B>` co-iterated with a streamed provider input —
     one mechanism, not two.
   - `reverse<I>` and future arbitrary orders: chunk-hostile; the provider
     answer is an LRU chunk cache (P3) or forced materialization, chosen by
     a simple order-vs-chunking classification. The classification is the
     new compiler piece: (source order, store chunking) → {sequential,
     blocked, cached, materialize}.
4. **Windowed folds.** `let static` over window×provider co-iteration folds
   only the window's cells — small windows of huge variables become
   foldable (today the whole-variable payload must clear the 65536 ceiling).
5. **`read_window` disposition.** The surface verb is TRANSITIONAL: its
   typing arm and its chunk-skipping emitter become the implementation of
   (1)+(2); the verb is removed once window sources parse. No further
   subsetting verbs get added to providers.

## Explicit non-goals / corrections

- **No "provider-backed virtual array" carrier.** Provider variables stay
  content handles with force points (`.read` materialize, `.stream` inline,
  `let static` fold). They are never `Void`-element and never erase.
- **No write-through views.** Virtual arrays have no content to write
  through; writing stays a verb (`.write`) and, later, the write-terminal
  form (StreamingIONotes.md P5).
- Boolean-mask domains remain CompoundIdx territory (`range<CompoundIdx<m>>`
  already exists and erases to the present-cell loop) — windows and masks
  compose at the index-type level, not in the provider.

## Implementation seams (when this is picked up)

- Parser/TypeCheck: window form of the virtual-source syntax (`range<I>` and
  anonymous ranges already parse; windows add two static bounds).
- IR: `VirtualKind.VirtualRange of offset` generalizes to carry (lo, hi) —
  the loop-bound emission and the element bindings already flow through
  `genElementBindingNew`'s virtual arms.
- Providers: no contract change — the streamed fiber reads and the window
  chunk-skip logic already take coordinates/domains from the caller.
