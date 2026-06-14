# Phase 2 certification review brief (close gate)

Adversarial review of the IMPLEMENTED Phase 2 code. Read-only; output findings only.

## What shipped

`spectra_data.parquet` now carries point-layout signal for ALL in-range spectra (profile via
ReadMZData(centroid=false)); `spectra_peaks.parquet` carries Thermo CentroidStream label peaks only for
profile scans with HasCentroidStream; `spectra_metadata` emits one `spectrum` row per output ordinal
(index 0..N-1, id, time). mz f64 / intensity f32; Thermo-native ascending m/z with sorting_rank:0;
index data_kind "peaks". 34/34 tests pass; mzpeak-validate → only the expected Phase-3 scan-facet error.

## Read (repo root /Users/kohlbach/Claude/mzPeak4TRFR)

- ThermoRawFileParser/Writer/MzPeakSpectrumWriter.cs   ← main writer (dual rep + per-ordinal metadata loop)
- ThermoRawFileParser/Writer/MzPeak/MzPeakParquet.cs   ← helper (should be reused, not duplicated)
- ThermoRawFileParser/ThermoRawFileParserTest/  (the new Phase-2 NUnit tests)
- .planning/phases/02-spectra-signal-data/02-01-PLAN.md and 02-01-SUMMARY.md and REVIEW-SYNTHESIS.md
- refs/_findings/mzpeak_groundtruth_schema.md

## Evaluate

1. CORRECTNESS: the per-scan loop, filtering (MaxLevel/MsLevel/scan range), dual-representation routing
   (profile→data; CentroidStream→peaks only when ScanData==Profile && HasCentroidStream; centroid-only→data
   only, no duplication). Resource disposal (streams/zip/MemoryStream). Exception paths and the
   no-matching-scan / zero-point guards still hold from Phase 1.
2. CONSISTENCY: spectra_data and spectra_metadata cover the IDENTICAL spectrum set; spectrum_count == distinct
   spectrum_index in both facets; spectra_peaks listed in index only when written.
3. WIDTHS/ORDER: mz f64, intensity f32 uniformly; m/z non-decreasing per spectrum; multiset preserved (no
   drop/merge); sorting_rank:0 declared.
4. HELPER REUSE & FORWARD FIT: no duplication of MzPeakParquet; is the metadata-writing code shaped so Phase 3
   (adding scan/precursor/selected_ion parallel facets + rich spectrum fields) can extend it cleanly?
5. BLOAT/STYLE: dead code, over-engineering, redundant comments; any comment referencing harness/process/phases
   (forbidden) — exact lines. BOM on NEW files.
6. TEST QUALITY: do the 7 new tests actually lock dual routing, per-ordinal metadata parity, width coercion,
   filter exclusion, and duplicate-m/z survival — or are any vacuous?
7. MEMORY: full in-memory + single row group is the accepted v1 limitation — flag only if it breaks
   correctness (not just scale).

## Output

Prioritized findings: SEVERITY (BLOCKER/HIGH/MEDIUM/LOW/NIT), file:line, problem, concrete fix. End with one
line: VERDICT: CERTIFY / CERTIFY-WITH-FIXES / REWORK.
