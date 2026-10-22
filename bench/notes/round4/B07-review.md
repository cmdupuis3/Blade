# B07 root source review

Source checkpoint reviewed 2026-10-22 in b-measurement at b02ac335 plus the frozen
bench-only output-boundary bundle. Handoff SHA256
36429D225D636067118D6B94C45CF5952861C96ED705C13870F336DF12C617B8.
No production compiler/runtime/harness change; B04 performance eligibility stays false.

Read ABI, strict adapter, driver, separate observer, Fortran task, runner, validator,
fixtures, frozen task entry/helper/attribute boundaries and handoff. The frozen
manifest is 56B53905E7A28D96B1AE4F1A3257EA47BECE002BAA1D26F7FE970EDFD83082C9.
Unknown raw inputs refuse before parsing. The exact helper/arithmetic prefix is
retained; only the timer/main/reporting wrapper changes. LLVM's new task/cleanup
do not inherit the original main's nofree attribute. C++ typed release, LLVM's
aligned allocator pair and Fortran allocatable release remain matched.

The driver receives four disjoint roots and two completed scalar values through
an opaque publication call before task return. It checks all cells after t1,
performs independent output-corruption and input-mutation controls, streams JSON
before release, then checks four malformed descriptors and duplicate cleanup.
Those trusted-descriptor checks are not a general ownership registry. The
observer is a separate native object, so source corrupt/check/restore cannot
be accepted merely because a compiler folded the check. Actual native evidence
is still required. Prepared-input mutation remains a distinct later artifact.

Approve stage 1 only: sole Luna runs pure fixtures, then at most eight canonical
native tasks, C++/LLVM/natural/expert by licensed/repro, one each. Follow the frozen
handoff/runner, private absolute paths, reliable stdout/stderr capture, exact
tool/source/object/executable identities and stop on first unexpected failure.
No .NET build, new Blade emission, full suite, performance sweep or prepared arm.
Runner SHA256 77E15567DC72D8FA9EADEB852AE6499271887E818326EDA077F3CD46D445143B.

Each numeric/provenance gate must pass before advancing. Root then audits saved
native object formats/sections and linked executable: separate no-LTO boundary,
t0 -> real task -> t1, both complete store streams, final checksum arithmetic and
publication before return, actual full-buffer loads afterward, and matched release
after observation. Numeric success alone leaves enclosure null and acceptance
false. Saved-output audit validation must not rerun successful task processes.
Even acceptance remains diagnostic-only, never an ordinary Blade speed result.

Current result: source approved; execution and native review pending. Full suite0.

Checkpointed as25c14a87 plusfa4fa882. The latter explicitly tracks the unchanged
manifest-pinned LLVM shim otherwise hidden by the repository's generated-shim
ignore rule. All27 frozen files are now tracked; no bundle byte changed.

Stage1 stops before native compilation:14 pure fixtures,13 pass/1 error. Both
pinned C++ inputs end at the final brace without a trailing newline; the adapter
mistakenly requires one. Root inspected the raw suffix and reviewed the one-line
exact-suffix repair plus regression assertions. Unknown hashes/manifest still
refuse; no frozen input/helper/arithmetic prefix changes. Revised adapter
F90C7DF3CC8297A1F090144DE4873722AEE096CAD8263A7A9F5419B44879C925;
fixtures01B946E51D8E8CAEBE101BB683C3CBAADD531A90D006EF69179B2EB851DF2FFF;
handoffE73E0AA82BC48BC047DA7016999CD0C38D874554A85893D313DB212CBF90C925.
Approve the same staged primary request in newB07-runner-a2, preserving initial
failure logs. No successful task process is being repeated: none ran initially.

Canonical native review is accepted and all eight saved-output validators passed:
numeric, identity, native enclosure and diagnostic acceptance are true; performance
eligibility remains false. Root read each saved result in integration
bench/.build/B07-saved-validation. No successful native canonical arm was repeated.
The hash-bound native-review/evidence documents remain immutable.

One additional prepared LLVM repro arm is authorized after D08 Zarr using the
separate B07-prepared-dispatch-proposal.md. Its pre-producer x[1026]+=0.125
callback must affect the actual p/q stores and both completed checksums inside
the measured call. This is a separate dependency witness with new object/native
audit identities; canonical addresses cannot be reused. No other prepared arm,
canonical rerun or performance sweep is authorized.