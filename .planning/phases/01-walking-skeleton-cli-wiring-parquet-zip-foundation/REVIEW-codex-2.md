Plan refs are `01-01-PLAN.md`.

- F1 RESOLVED — public helper/struct required at lines 198-200.
- F2 RESOLVED — DataField identity, no path splitting at 106-111 and 218-220.
- F3 RESOLVED — net8 ref-pack vs roll-forward clarified at 293-305.
- F4 RESOLVED — `ZipArchive(... leaveOpen: true)` and dispose-before-flush at 276-281.
- F5 RESOLVED — selects first real point-bearing scan; no fabrication at 241-251.
- F6 RESOLVED — no points throws `RawFileParserException`, no archive at 249-250.
- F7 RESOLVED — metadata facet count set to `"0"` at 264-268.
- F8 RESOLVED — gate asserts both Parquet entries are ZSTD at 286.
- F9 NOT-RESOLVED — concrete field list still says `string`, not `string?`, for nullable PARAM fields at 199-202.
- F10 RESOLVED — named `Column(...)` helper + def/rep lock at 186-188, 211-214.
- F11 RESOLVED — pre-normalized label contract at 206-210.
- F12 RESOLVED — README help update required at 150-151.
- F13 RESOLVED — verify greps `"4 for mzPeak"` in MainClass and README at 169.
- F14 NOT-RESOLVED — verify checks RC/no `ERROR`, but still does not assert `0 warnings`; warning claim remains prose at 286, 288, 338.
- F15 RESOLVED — exact `spectrum_array_index` JSON is inlined at 257-263.

NEW issues:
- `files_modified` omits `ThermoRawFileParser/ThermoRawFileParserTest/MzPeakParquetTests.cs` despite Task 2 creating it, lines 7-15 vs 177-178.
- Task 3 redirects only stderr to `run.log`; log4net console output is stdout by default, so the no-`ERROR` grep can miss emitted log lines, line 286.

VERDICT: SHIP-WITH-FIXES.
