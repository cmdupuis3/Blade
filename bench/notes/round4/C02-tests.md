# C02 validation results

Status: both the original and syntax-corrected frozen snapshots failed the Release build; `test optimize` and all later C02 checks were not run. No C02 source files were modified by the runner.

The initial frozen handoff hash matched `D107518B2649E51ECE2EA465A958E9B556AD182293C4480DFD7C2CA665F41884`, and all eight initial source/test hashes listed below matched. The corrected handoff hash also matched: `50288EB28826CD0693A9477446B1FB380DFACCF462DABA4CEE452504FF0817C8`. Its `tests/OptimizeTests.fs` hash was `33568419ABC356D9CFAE084DD7222C3DEE503CBBFE5E336E909BC37C0A257FA3`; the production and corpus hashes were unchanged from the table.

| File | SHA256 |
|---|---|
| `src/Optimize.fs` | `45EDD5C6E608683811FFD9268D2D5287479E12654C85F6054C089F4D59B8FA7D` |
| `tests/OptimizeTests.fs` (initial snapshot) | `275E8B9177CBDF9951617BBB96238B1A44655AF3043537AC48B656C0CBFD167F` |
| `tests/corpus/sql-reduce/031_private_partial_map_seeded_rows.blade` | `59C26083962399AFA584B829629BE8F1DDEBE225B3B053E12C49CEBF6B38AC5D` |
| `tests/corpus/sql-reduce/032_private_partial_ordered_rows.blade` | `3C74FFF3CB99EBF120AC144736CEBA0A165E3438D675C35282A4084E5701258D` |
| `tests/corpus/sql-reduce/033_private_partial_zero_columns.blade` | `4DB5A4FB85B0A1CF4AC0E7A7A104A595E465DB72909D0C3F4843A6313B800B22` |
| `tests/corpus/sql-reduce/034_private_partial_zero_rows.blade` | `9BE0D499C112A74BA00FE62335FA399C349EB8389F99C1D510ABF501746124ED` |
| `tests/corpus/sql-reduce/035_demanded_partial_map_retains_matrix.blade` | `0B6A537AA8345AF50F0BA205EDEC8FDCDAA928B80C7FCA879578E0B260D240D8` |
| `tests/corpus/sql-reduce/036_private_partial_producer_failure_first.blade` | `DE4ECE687ACD7F090D97A2211EFC929A6EDF6D5A5395CC47F69F08D2199ACC15` |

Command: `dotnet build C:\Users\cdupu\Documents\GitHub\Blade\.round4-worktrees\c-terminals\Blade.fsproj -c Release`

Working directory: `C:\Users\cdupu\Documents\GitHub\Blade\.round4-worktrees\c-terminals`. Private TEMP/TMP and all runner outputs were under `C:\Users\cdupu\Documents\GitHub\Blade\.round4-worktrees\c-terminals\bench\.build\C02-runner`. Complete build output: [`build-D107518B.log`](../../../../c-terminals/bench/.build/C02-runner/build-D107518B.log). Provenance and exit status: [`build-D107518B.provenance.txt`](../../../../c-terminals/bench/.build/C02-runner/build-D107518B.provenance.txt). The build exited 1 after 48.40 seconds with 4 warnings and 3 errors. NU1900 warnings report that the NuGet vulnerability feed could not be reached.

The initial three compile errors were FS0001 in `tests/OptimizeTests.fs` at 1606:103, 1607:71, and 1608:102. The list was inferred from a two-element tuple (`string * IR.IRModule`), while those entries contained three elements (`string * 'a * IR.IRModule`).

The corrected snapshot used the same Release build command and working directory. Its full output is [`build-50288EB2.log`](../../../../c-terminals/bench/.build/C02-runner/build-50288EB2.log), with provenance in [`build-50288EB2.provenance.txt`](../../../../c-terminals/bench/.build/C02-runner/build-50288EB2.provenance.txt). It exited 1 after 22.80 seconds with 4 NU1900 warnings and 3 errors: FS0003 at `tests/OptimizeTests.fs` 1606:40 (a tuple value is applied as a function); FS0001 at 1618:95 (expected `IR.IRCallable list`, received `Map<string,Types.IRId>`); and FS0001 at 1618:108 (expected `IR.IRModule`, received `TypedLowerEnv`). Neither snapshot produced a usable binary. The `optimize` category and subsequent requested checks remain pending another corrected frozen source snapshot.
