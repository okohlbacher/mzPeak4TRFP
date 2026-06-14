# Phase 1 plan — adversarial review synthesis (plan gate)

Three independent reviews of `01-01-PLAN.md`:
- **codex** (`codex exec`, read-only): VERDICT **REWORK** — 2 BLOCKER, 4 HIGH, 4 MEDIUM, 2 LOW.
- **vibe** (`vibe -p --agent plan`): VERDICT **SHIP-WITH-FIXES** — 2 HIGH, 1 MEDIUM.
- **GSD plan-checker**: **PASS with warnings** — 3 mechanical (W1–W3).

Raw outputs: `REVIEW-codex.md`, `REVIEW-vibe.md`, `PLAN-CHECK.md`.

## Consolidated findings → resolutions (deduped, prioritized)

| # | Sev | Finding (source) | Resolution to bake into plan |
|---|-----|------------------|------------------------------|
| F1 | BLOCKER | `MzPeakParquet`/`MzPeakParam` default to `internal`; test assembly can't compile against them (codex 1) | Declare `public static class MzPeakParquet` + `public struct MzPeakParam` |
| F2 | BLOCKER | Column lookup by `/`-separated `Path.ToString()` is wrong; Parquet.Net paths are dot-separated (codex 2) | Don't string-split paths. Iterate `schema.GetDataFields()` and supply each `DataField`'s matching array directly; key the write helper on the `DataField` objects themselves |
| F3 | HIGH | net8 build: `DOTNET_ROLL_FORWARD` does not supply the missing net8 **ref/targeting pack** (codex 3, plan-checker W3) | Build relies on NuGet auto-restore of `Microsoft.NETCore.App.Ref` 8.x; if that fails, install the .NET 8 SDK. Roll-forward is for **run/test** only. Put the env exports IN the verify commands |
| F4 | HIGH | `ZipArchive` disposal closes the base stream; `ZipFile.Open` needs a private path (codex 4) | Use `new ZipArchive(Writer.BaseStream, ZipArchiveMode.Create, leaveOpen: true)`; dispose archive, THEN `Writer.Flush()/Close()` |
| F5 | HIGH | "Write one base-peak point" when zero array points = fabrication; base-peak fields may be null (codex 5, vibe HIGH-1) | Select the first in-range spectrum **that has ≥1 real point**; never synthesize. Remove the base-peak fallback |
| F6 | HIGH | No defined behavior when filters exclude every scan; OPEN needs `spectrum_metadata` len ≥ 1 (codex 6, vibe HIGH-2) | If no in-range spectrum with points exists, throw a clear `RawFileParserException` — emit no broken archive. Add this branch explicitly |
| F7 | MED | metadata facet `spectrum_data_point_count` should be `0` (ground truth); real count lives on the data facet (codex 7) | Set metadata-facet count KV to `0`; real point count on data facet. Confirm via OPEN gate |
| F8 | MED | PQ-03 gate never asserts internal codec; Parquet.Net default is Snappy → could OPEN while violating PQ-03 (codex 8) | Add a gate step: read both parquet entries' metadata and assert column-chunk compression == ZSTD |
| F9 | MED | `MzPeakParam` string/accession/unit must be nullable per PARAM schema (codex 9) | Keep them nullable reference fields; add a round-trip test with null accession/unit/string |
| F10 | MED | `DataColumn` def/rep-level arg order is ambiguous in docs (codex 10) | Hide behind a named helper `Column(field, defined, defLevels, repLevels)`; lock semantics with the round-trip test |
| F11 | LOW | `CvColumn` label normalization undefined (codex 11) | Contract: callers pass pre-normalized snake_case labels; document on the helper. No normalization logic |
| F12 | LOW | `ThermoRawFileParser/README.md` help text left stale at "4 for None" (codex 12) | Update the README `--format` help line in Task 1 |
| F13 | WARN | Task 1 verify doesn't assert help text contains mzPeak (plan-checker W1, CLI-02) | Add `grep -q "4 for mzPeak" MainClass.cs` to Task 1 verify |
| F14 | WARN | Clean-run `Errors==0/Warnings==0` only in prose (plan-checker W2) | Task 3 verify asserts the run exits 0 and emits no ERROR line |
| F15 | MED | `spectrum_array_index` JSON not inlined → transcription risk (vibe MED) | Inline the exact `spectrum_array_index` JSON value in the plan from RESEARCH so the executor copies, not retypes |

All 15 fed back to the planner for a revised `01-01-PLAN.md`. Re-review after revision before execution.
