# A03: consume an existing LLVM buffer-reuse plan

Proposed next lane-A card, after A02's initial census. Begin with source/design
only; do not change the frozen A02 controls or execute anything. Read A02-tests.md
and A02-review.md in integration and the exact pinned emissions in the A checkout.

A02 establishes that C++ shares two backing buffers across three/five map stages,
while LLVM allocates each stage despite the same applied donor plan. Ordinary
array returns also ignore a planned donor. This is a more concrete next storage
opportunity than introducing a new general last-use algorithm.

Propose one minimal LLVM plan consumer using existing planner legality, with
explicit numeric IR identity to actual owned backing identity. Establish when the
donor is materialized, which scope owns it, how one allocation can back successive
slots, and how cleanup/return escape handles the root exactly once. A decision
record alone is insufficient: actual storage must match or admission must decline.

Start with plain dense static scalar arrays already admitted by planPoolReuse.
No same-cell destructive update, compatible-capacity reshape, dynamic extent,
caller-owned donation, gather/overlapping view, new alias proof or lifetime-event
admission. A01 event-bearing functions retain their current refusal. Identify
where late AVirt evaluation could read an overwritten donor, and prove that every
admitted prior value has already been consumed or is truly dead.

Keep two separate A02 findings explicit: tuple calls currently inline into main
without a general call cleanup scope; retained alias 029 is a C++ copy whose old
p backing stays in a conservative escape group. Neither is silently solved by a
reuse consumer. If safe reuse needs one of those repairs first, report the exact
dependency and choose one smaller ownership card before implementation.

C02-L1 is concurrently changing applyToArr's row-valued input forcing in the C
checkout. Read its final review before choosing edits; no duplicate fix, merge or
source edit during this census. Root will combine the accepted changes before
any LLVM production implementation. Required output identity, failure order and
producer/consumer numeric graph must remain unchanged.

Return exact source anchors, proposed owned files, one positive and strongest
refusals, emitted backing/free expectations and a bounded sole-Luna validation
request. No performance claim, full suite or self-assigned follow-on work.
