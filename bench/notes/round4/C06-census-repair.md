# C06 census fixture repair

The remaining census reached06 after final02-05 were captured. Input06 failed
parsing at6:9 because `zero` is reserved; no lowering06 occurred. Saved evidence
`bench/.build/C06-census-runner/06-final-r2.stderr.log` has SHA256
`2EDB93B135C2A15DB1EDD489673CFA7B2C33CBFDAF0C4559EB6CA529E595592A`.

Only input06 changes: rename both `let zero` binders and their denominator uses
to `denom`. Line count/newlines, arithmetic, branch/order, function names, TEST
header and ABORT pin are unchanged. Reversing exactly those four token edits
reproduces the original text byte-for-byte under its unchanged UTF-8 encoding.
No production, adapter or old census note is edited. Failed artifacts stay intact.

Original input SHA256: `23BAAB7DFE6BEDCA12258861AE6E90E3813C1AA513B9F7116C2F8FDA042222B2`.
Current input SHA256: `1C76193C8AC3EB01341EEA53A1D75CAB46A1C299C2D4A3A03FAA4CCACE5A6E44`.
The existing census manifest updates only input06's hash; all other entries are
unchanged. Root reviews/checkpoints before Luna resumes06 in a fresh directory.
Never replay final02-05. No build/lower/emit/run/test/Git mutation by coder.
`.r4tmp` and all runner artifacts are preserved. The implementation proposal
remains conditional on outstanding controls; no production authorization inferred.
