# v2 Phase 1 plan review synthesis (plan gate)

codex = REWORK (6 findings). vibe = hung/no-output (flaky again; bp37ltan6) — codex findings are
comprehensive; fold vibe in if it lands before execution.

## Fixes to fold into 01-01-PLAN.md

| # | Sev | Finding (codex) | Resolution |
|---|-----|-----------------|------------|
| S1 | BLOCKER | Streaming footer KV (`spectrum_count`, point counts, `chromatogram_tic_source`) is only known at FINALIZE, but Parquet `CustomMetadata` must be set before the writer is disposed — the plan opens the streaming writer before these are known | Streaming handle supports setting final `CustomMetadata` AFTER all row groups, BEFORE dispose: `CloseAsync(finalMetadata)` (or settable `handle.CustomMetadata` then dispose). Add a test: write row groups → set final metadata → dispose → read footer KV back. |
| S2 | HIGH | `_precursorScanNumbers[filterKey] = scanNumber` mutates shared state PRE-commit (MzPeakSpectrumWriter.cs:189), so a skipped scan can still influence later precursor resolution even though no rows committed — breaks all-or-nothing | Stage the precursor-map update locally; apply ONLY in the success-commit block (or roll back in `catch`). Test: force a failure after this point, prove later scans don't resolve through the skipped scan. ALL staged state (data, peaks, metadata, precursor/selected_ion, scanNumber→ordinal map, TIC point) commits atomically. |
| S3 | MED | Disposal ordering of `ParquetWriter` + temp `FileStream` underspecified — `File.OpenRead(temp)` before flush/close → truncated bytes / sharing error | Streaming handle OWNS writer+stream; finalize order = flush residual row group → set final metadata → dispose writer → flush/dispose temp FileStream → `AddStoredFromFile` → delete temp in `finally`. |
| S4 | MED | Streaming `chromatograms_data` may drop `BuildChromatogramDataFacet`'s `CollectPrefix` side-effects (MzPeakSpectrumWriter.cs:409) that feed the metadata `cv_list` → validation regression | Factor chrom-data schema + CURIE-prefix registration into a helper used by both the streaming path and tests; call prefix registration BEFORE `BuildMetadataFacet`. |
| S5 | MED | `DataFacetMultiRowGroup` test not decisive: fixed `RowGroupRowCap = 1_048_576` won't trigger multiple row groups on `small.RAW` | Make the cap injectable/internal for tests (or a writer-level synthetic test that forces ≥2 row groups through the real flush path). |
| S6 | LOW | Task 2 verify validates `/tmp/small-stream.mzpeak` but never creates it (tests stale output) | Make it self-contained: the command creates the archive (CLI convert) or the NUnit test creates+validates that exact file. |

S1 and S2 are load-bearing (S2 is the core all-or-nothing correctness). After revision, recommend a codex
confirm before execution (the customMetadata-timing + atomic-commit are the make-or-break details).
