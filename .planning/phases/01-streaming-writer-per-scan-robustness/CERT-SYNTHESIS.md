# v2 Phase 1 certification synthesis (close gate)

codex = CERTIFY-WITH-FIXES (6). vibe = empty/broken this session (exit 0, 0 bytes — documented; codex carried).
60/60 tests native; MEM-02 954MB→2.04GB RSS validate 0/0; Astral 8.4GB no-OOM (--quick 0/0).

| # | Sev | Finding (codex) | Fix |
|---|-----|-----------------|-----|
| C1 | HIGH | `BuildPrecursor` (MzPeakSpectrumWriter.cs:674) computes selected-ion intensity from `parentScan` even when the parent was NOT committed (scanNumberToOrdinal miss). A skipped/bad parent then forces a later readable child to read the bad parent via `CalculatePrecursorPeakIntensity` → the child also fails. Breaks skipped-scan ISOLATION (the headline guarantee) | Only compute precursor/selected-ion intensity when the parent was emitted (e.g. gate on `rec.PrecursorIndex.HasValue` / a scanNumberToOrdinal hit); OR catch parent-intensity read failures and leave `SelectedIonIntensity` null. A skipped parent must never make a child fail. |
| C2 | MED | `PointFacetStream.Append` (748) appends a whole scan before checking `_cap`, so a row group can overshoot `RowGroupRowCap` by a full scan and the cap isn't a real bound (test cap not enforced) | Chunk inside `Append`: fill remaining capacity, flush the row group, continue with the SAME ordinal (a scan may span row groups). Makes the cap a hard memory bound and the multi-row-group test decisive. |
| C3 | MED | Temp facet streams constructed BEFORE the `try/finally` (174); if `peaksFacet`/`chromFacet` open throws, already-created temp files/handles leak | Init facet handles to null, construct INSIDE the protected try; ensure each temp file+handle is deleted/disposed in `finally` even on partial construction failure. |
| C4 | MED | The skipped-scan precursor-isolation test (MzPeakWriterTests.cs:1585) is a seam test — never drives `Write`/the real scan loop or `BuildPrecursor`, so it wouldn't catch C1-class regressions | Make it exercise the REAL writer path (or an extracted scan-processing helper) with a read/build failure after filter-key staging, then assert rows/ordinals/maps AND that a later child does not resolve/read through the skipped parent. (This test should fail before the C1 fix and pass after.) |
| C5 | LOW | `SmallRaw_Identical_To_V1_Invariants` (1522) asserts self-consistency but not the known v1 baseline counts/footer KV → would miss self-consistent semantic drift | Assert the known small.RAW v1 totals (spectra count, data-point total, peak-point total, TIC points) and footer keys for ALL streamed facets (spectra_data, spectra_peaks, chromatograms_data). |
| C6 | NIT (constraint) | Phase/requirement IDs in code/test comments: `S2` (MzPeakSpectrumWriter.cs:274), `ROB-01`/`ROB-02` (MzPeakWriterTests.cs:1627,1640) | Remove all phase/req IDs from comments; state behavior directly. Grep the phase diff for any other `S[0-9]`/`ROB-`/`MEM-`/`phase`/`harness` comment leaks. |

All FIX NOW. C1 is load-bearing (the isolation guarantee); C4 makes it test-provable. Re-verify: build,
full suite green native, mzpeak-validate 0/0 on small.RAW + 70JG_02, parity holds.
