# Phase 1 certification review brief (close gate)

Adversarial review of the IMPLEMENTED Phase 1 code. Read-only — output findings only, modify nothing.

## What shipped (claims to verify)

Phase 1 added a `-f mzpeak` output to ThermoRawFileParser that emits a STORED ZIP of ZSTD-internal
Parquet facets (`spectra_data.parquet` + `spectra_metadata.parquet`) + `mzpeak_index.json` from one
real spectrum, openable by the reference reader. 27/27 tests pass.

## Read these (repo root /Users/kohlbach/Claude/mzPeak4TRFR)

- ThermoRawFileParser/Writer/MzPeakSpectrumWriter.cs   ← main writer
- ThermoRawFileParser/Writer/MzPeak/MzPeakParquet.cs   ← low-level Parquet/CV helper (Phase 3 depends on it)
- ThermoRawFileParser/ThermoRawFileParserTest/MzPeakParquetTests.cs
- ThermoRawFileParser/OutputFormat.cs, RawFileParser.cs, MainClass.cs, Writer/SpectrumWriter.cs (wiring)
- .planning/phases/01-walking-skeleton-cli-wiring-parquet-zip-foundation/01-01-PLAN.md (the contract)
- refs/_findings/mzpeak_groundtruth_schema.md (target schema)

## Evaluate

1. CORRECTNESS: bugs, resource leaks (streams/ZipArchive/MemoryStream disposal), async-over-sync hazards
   (`.Result`/`.Wait()` deadlock risk), encoding/BOM, exception paths, the empty/zero-point and
   no-matching-scan branches. Does the writer actually honor m/z=f64 / intensity=f32 and STORED+ZSTD?
2. HELPER QUALITY (forward-looking): is `MzPeakParquet` (BuildParamField, CvColumn, Column, WriteAsync)
   a clean, sufficient foundation for Phase 3's packed parallel tables (4 nullable top-level structs,
   lists-of-structs, per-row null discipline)? Any API shape that will force a rewrite in Phase 3?
3. CONFORMANCE: anything that would make a *fuller* archive fail `mzpeak-validate` or diverge from the
   ground-truth schema (column names, CURIE form, types, index json shape).
4. BLOAT / STYLE: dead code, over-engineering, redundant comments, and especially any comment that
   references harness/GSD/process/phases (forbidden) — flag exact lines.
5. TEST QUALITY: do the tests actually lock the claimed behavior, or are any assertions vacuous?

## Output

Prioritized findings: SEVERITY (BLOCKER/HIGH/MEDIUM/LOW/NIT), location (file:line), problem, concrete fix.
End with one line: VERDICT: CERTIFY / CERTIFY-WITH-FIXES / REWORK.
