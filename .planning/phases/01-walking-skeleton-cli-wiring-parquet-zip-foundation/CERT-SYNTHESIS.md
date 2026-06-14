# Phase 1 certification synthesis (close gate)

Inputs: `CERT-codex.md` (CERTIFY-WITH-FIXES), `CERT-vibe.md` (CERTIFY-WITH-FIXES), and the
authoritative `mzpeak-validate` run on a fresh `small.mzpeak`.

**Validator verdict:** FAIL (2 errors, 1 warning) — expected pre-Phase-3 (Phase 1 only promised
reader-OPEN, which passes; full validator pass is VER-01 / Phase 4). The findings front-load the
Phase 3 contract.

## Consolidated findings → disposition

| # | Sev | Finding (source) | Disposition |
|---|-----|------------------|-------------|
| G1 | HIGH | No-matching-scan / zero-point path opens output first, then throws → leaves a zero-byte/partial `.mzpeak` and an unclosed `StreamWriter` (codex) | **FIX NOW** — construct facet bytes BEFORE `ConfigureWriter`; on failure write no file; ensure stream closed (try/finally) |
| G2 | HIGH | spectrum `id`/`time` emitted nullable (def levels {2}) but ground truth requires them (vibe V2/V3) | **FIX NOW** — non-nullable, def levels {1} |
| G3 | HIGH | `WriteAsync` awaited then blocked via `GetAwaiter().GetResult()` without `ConfigureAwait(false)` (codex LOW / vibe HIGH) | **FIX NOW** — add `ConfigureAwait(false)` to helper awaits (full async propagation needs ISpectrumWriter signature change → out of scope) |
| G4 | ERROR(validator) | `mzpeak_index.json` requires a `version` property | **FIX NOW** — add `version` (+ `metadata.version`) to the index |
| G5 | MED | PARAM "fully populated" test sets multiple value leaves in one row → doesn't lock union-as-struct discipline (codex C4) | **FIX NOW** — one populated value leaf per row, null def levels for the others |
| G6 | LOW | Hardcoded `SpectrumArrayIndex` JSON string inline (vibe V6) | **FIX NOW** — extract to a `const` |
| G7 | MED | BOM on edited files OutputFormat/MainClass/RawFileParser/SpectrumWriter (vibe V5) | **VERIFY ONLY** — these are pre-existing upstream TRFP files that ship with a UTF-8 BOM; matching surrounding style = keep. Confirm our NEW files (MzPeakSpectrumWriter.cs, MzPeakParquet.cs, MzPeakParquetTests.cs) are BOM-free |
| G8 | MED | No integration test for the CLI/writer path (OutputFormat.MzPeak dispatch, archive structure, ZSTD) (codex C2) | **DEFER → Phase 4** — this is VER-03 (NUnit conversion + structure + `mzpeak-validate`); note as Phase 4 input |
| G9 | ERROR(validator) | `spectra_metadata` requires a `scan` facet | **DEFER → Phase 3** — metadata facets are Phase 3; the validator requires at minimum spectrum + scan structs. Capture in Phase 3 context |
| G10 | INFO(validator) | `sorting_rank` null/absent on `spectra_data.point.mz` | **DEFER → Phase 2** — Phase 2 sorts m/z ascending and should declare `sorting_rank: 0` in the array index |

Fix-now set: G1, G2, G3, G4, G5, G6 (+ G7 verify). Deferred with capture: G8 (P4), G9 (P3), G10 (P2).

After fixes: rebuild, full test suite green, re-run `mzpeak-validate` and confirm the `version` ERROR
and `profile_resolution` WARNING are gone (the remaining `scan`-facet ERROR is the expected P3 gap).
Then certification = PASS for Phase 1's defined scope.
