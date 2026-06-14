# Phase 2 certification synthesis (close gate)

vibe = CERTIFY (no findings). codex = REWORK (1 BLOCKER, 1 MEDIUM). Raw: `CERT-codex.md`, `CERT-vibe.md`.
Validator: FAIL with only the expected Phase-3 `scan`-facet error (sorting_rank INFO resolved).

| # | Sev | Finding (codex) | Fix |
|---|-----|-----------------|-----|
| C1 | BLOCKER | Zero-point spectra (`mz.Length==0`) still get a metadata row + ordinal (only `ordinal==0` guards it), so an empty in-range scan makes `spectrum_count > distinct point.spectrum_index` → data/metadata parity broken | Only append the metadata row AND increment the ordinal AFTER the data facet received ≥1 point for that scan. Skip zero-point scans entirely (they appear in neither facet). Throw `RawFileParserException` if NO point-bearing in-range spectrum exists. Keep data/metadata spectrum sets identical |
| C2 | MED | Peaks test passes vacuously if `spectra_peaks.parquet` is absent — doesn't lock dual routing / no-duplication | Assert small.RAW DOES write `spectra_peaks`; enumerate expected qualifying ordinals (`ScanData==Profile && HasCentroidStream`) and compare to distinct `spectra_peaks.point.spectrum_index`; assert centroid-only scans are absent from peaks |

Both FIX NOW. Re-verify: build, full tests, `mzpeak-validate` (only expected scan-facet error), and a parity
assertion `spectrum_count == distinct spectrum_index` in both facets on small.RAW.
