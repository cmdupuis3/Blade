# B04 design disposition: two required store streams

Root read B04-census.md, B04-contract.md and B04-fixture-sketches.md at source
`a7048cd2`. Approved one diagnostic-only implementation, not timed-harness admission.
The lane was updated to merged checkpoint `2fd60cb8`; its tracked tree exactly
matches integration `60e320f6`, including accepted A01 ownership fixes.

The control computes p = 1.5*x+y and q = p*p, retaining both full arrays plus
the output and input checksums. Natural and expert mirrors may differ in whether
they reread p, but both must store p and q. The existing saxpy scalar-consumer
control does not establish that second required store stream.

The current scalar-only benchmark parser has no opaque post-stop full-buffer
observer. Full-array diagnostic prints/dumps can validate this bounded control,
but neither a separate dump executable nor scalar checksums certify stores in
a timed executable. Timed admission remains gated on an explicit same-binary
observer design; no harness/compiler/runtime change is authorized here.

Implementation scope is one odd 2,053-cell diagnostic, independent R5 reference,
natural/expert Fortran mirrors and focused validators. Maximum initial matrix:
C++ and LLVM Blade plus gfortran natural/expert, each default and reproducible
contraction-off. Every arm must preserve the same dependency graph and required
outputs; exact store/observation/lifetime evidence is separate from numerical
validation. No larger sweep, ifx/OMP/BLAS expansion or performance claim.

Sole Luna execution follows review of the actual source and frozen handoff.

## Frozen implementation review

Root read the Blade and both Fortran sources, independent reference, validator, all
27 authored fixture methods, audit template and execution handoff. All 13 listed
source/dependency hashes match; handoff hash is
`CB91246CFFB8AA39D4A9E48F59DC7892A22778F933AC844AFFA674419B41357E`.
Private base TEMP/TMP now applies before fixtures/reference/build, and the compiler
build runs from the checkout root with absolute private logs.

Approved the bounded initial diagnostic matrix through numerical validation and saved
exact-executable evidence. Each arm is inspected before the next. An absent manual
audit deliberately yields exit 3 with unmeasured status; it is not a passing store
proof. Root must review actual stores and complete post-stop observation before any
manual attestation. No timed-harness admission, performance claim or expansion follows.

## Exact native review completed

Root independently read all eight actual native disassemblies, mapped the stored
inputs and distinct p/q roots through their post-stop observers, and checked exact
executable/stdout/dump identities against the numeric records. Each output has
2053 cells. Full annotations are saved as root-review.md in each immutable arm's
artifact directory beneath b-measurement/bench/.build/B04-runner. Executable hashes
are in B04-tests.md; disassembly hashes below bind the reviewed instruction evidence.

| Arm | Disassembly SHA256 | Actual route / ownership |
|---|---|---|
| C++ licensed | B0A7731641A7900A0A17D1B24B7CDD68108152D2E320EA1F5BFF27F2B3C65D9B | Separate passes; process-exit reclamation |
| LLVM licensed | 7558AC35E0C2635701EC78957DF8B55442487833F29FB572DC98BB13341CD986 | Separate passes; four native allocations; process-exit reclamation |
| gfortran natural licensed | CBC4C0CA103D0DD118385E3E273D2E8B487B873C0E63EF9BFC0A76262E893EC9 | Separate vector passes with scalar-tail forwarding; explicit release after observation |
| gfortran expert licensed | 37DD361B3ED8989F9B6C8B25E1B498EDFEC4A5E33C33A0C55C220E67542893C2 | Register forwarding, two output stores; explicit release after observation |
| C++ repro | B93C7A53B508D4A062A68D73D2A2DBCA77EF4C85F9EC59F0663ED384A3607DBC | Separate passes; process-exit reclamation |
| LLVM repro | DC7822F108D075915DBF3E8FAC71E4DA6B99C6961A87F082E84113B5E06BC727 | Separate passes; four native allocations; process-exit reclamation |
| gfortran natural repro | 1D5136F4873A806863DBFDCC30A6E2531607F55638DDE8780A1524D5ECCE40EB | Separate vector passes with scalar-tail forwarding; explicit release after observation |
| gfortran expert repro | D2B4EED06400A514135A9F66AA466EA6B738457C2D21307B0D735583AA042186 | Register forwarding, two output stores; explicit release after observation |

For reproducible C++, output stores214a/2182 and becb/bf07 precede stop bfcc;
post-stop loads c080/c120 visit both entire outputs. For reproducible LLVM,
stores1748..18ca and1924..1a7a precede stop1ccd; loads1cee/1d18 and1d46/1d68
observe every cell. Address prefix is14000. The natural Fortran repro stores
2bca/2bf9 and2c29/2c50 precede stop2d25, then observation2d70/2dd0 and frees
2e14..2e2c. Expert repro stores2bce/2bdc and2c21/2c31 precede stop2ce5,
then observation2d30/2d90 and frees2dd4..2dec. Native dump helper passes each
actual cell address to real-write; observers do not regenerate the arrays.

Licensed contraction is present where allowed; reproducible maps use separate
multiply/add and the folds preserve serial association. Numeric checks use the
documented tolerance and formatted output, not a bit-identity claim. In Blade's
C++ and LLVM executables, final scalar checksum arithmetic can occur after the
stop timestamp, including after array observation. Thus this review certifies
required stores and observation, not a fully enclosed checksum timing interval.

All eight store/endpoint manual gates are accepted. Sole-runner saved-output
validation of their bound audit records remains pending. No new native runs,
timed-harness admission, measured speedup, bandwidth or peak-memory claim follows.

Final acceptance: all eight saved-output validator invocations exited0 with numeric,
stores, endpoints and run_provenance=pass; diagnostic_accepted=true and
performance_eligible=false. Root inspected the resulting records and unchanged
source integration. Sourceb02ac335 integrated asde03addd. No native rerun occurred.
