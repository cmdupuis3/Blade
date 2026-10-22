# A02 review: barrier-chain census and controls

Source review accepted for staged diagnostics, not implementation or route success.
Base is A01 source `12b43b4f`; manifest
`137815a7b1f782128f367f9ce9eb9d8efc0f7fbba92f5b8dca7f2c0d174d6663`.
Root verified all seven content hashes and read the five fixtures, census and request.

The three- and five-stage chains retain each scalar observation and the final array;
each scalar affects the following map. Independent integer-valued pins agree with
the stated recurrences. The retained alias, ordinary array return and explicit
inline-region controls distinguish three different ownership conditions rather
than counting equal-sized arrays as interchangeable storage.

The census correctly separates planner root IDs, actual emitted backing aliases,
escape/free ownership and logical liveness. The absence of an LLVM plan consumer
is a source finding pending emitted evidence. Tuple-call temporary ownership is a
separate question. Neither is approved for implementation by this review.

Luna's first stage is build plus IR/plan/emission diagnostics only. If the default
optimizer removes the intended stages or no lifetime events survive, report that
shape and stop before the deferred correctness/category/sanitizer stage. Root
will review the emitted evidence and authorize the appropriate next stage.
No performance or process-capacity claim follows from these sub-16-KiB controls.

## Native correctness checkpoint

The corrected default-all-output batch passed all 24 arms: 027–030 reuse on/off
in both backends, and 031 reuse × lifetime-boundary settings in both backends.
Root inspected the actual runner invocation (no print override), full 027 output,
and the per-arm ledger. All pins and direct backend routes passed. The original
24 selected-output runs and two preliminary 027 controls remain historical and
are not counted again as all-output coverage. These results validate values, not
minimal lifetimes or measured savings.

Final disposition: accept A02 as an existing-route census and correctness control,
not an implementation change. Root moves the proposed broad category/sanitizer
follow-up to A03's actual ownership change, where these same fixtures remain
regressions. A02 changes no compiler/runtime source, and A01 already has its own
accepted ownership/category/sanitizer evidence; repeating those broad gates now
would not validate a new ownership implementation. No unrun check is claimed as a
pass. A03 must run the named ownership categories and supported sanitizer controls
on its changed, combined source before acceptance.

## Initial diagnostic disposition

Root read the actual pinned C++/LLVM ownership artifacts for 027-031 and the runner
ledger. All intended stages survive. C++ consumes the three/five-map reuse plans;
LLVM allocates distinct stage pools in both settings, including an extra ordinary
return pool in 030. Tuple-return bodies inline into main and retain their pools there.
These establish an LLVM plan-consumer gap and a separate tuple-call lifetime issue.

The retained-alias control 029 differs from its proposed physical-alias model:
IR has held->p, but C++ copies p into a fresh held allocation. Its conservative
escape group also keeps the now-unreturned p backing without local cleanup.
This is an evidenced existing behavior, not a claim of correct minimal lifetime.
The later reads use the preserved held copy; planned reuse is correctly refused.

031 retains the required explicit release and exact region-ownership refusal.
Root traced LLVM scratch allocation r4 through tracking slot r5 to its one release,
and C++ scratch v80 to its free before caller v37 allocation. Neither backend
reuses that released backing as a planned donor. Legs-off is a separate mirror.

Accepted sections 1-2 as a census, with these divergences. Section 3 remains the
bounded correctness/sanitizer follow-up; no implementation repair is authorized
by the census itself and no measured memory or performance saving is asserted.
