---
phase: 02-spectra-signal-data
plan: 01
subsystem: writer
tags: [mzpeak, parquet, spectra_data, spectra_peaks, dual-representation, centroid-stream, nunit]

# Dependency graph
requires:
  - "MzPeakParquet helper (Column/WriteAsync) from 01-01"
  - "MzPeakSpectrumWriter scaffolding (WriteFacet/AddStored/Leaf/SpectrumArrayIndex) from 01-01"
  - "SpectrumWriter.ReadMZData + ParseInput MsLevel/MaxLevel filters + Scan.FromFile/scanEvent.ScanData"
provides:
  - "spectra_data.parquet covering all in-range spectra (as-acquired point layout)"
  - "spectra_metadata.parquet with one spectrum row per output ordinal (index/id/time)"
  - "Conditional spectra_peaks.parquet from Thermo CentroidStream (profile + HasCentroidStream only)"
  - "sorting_rank:0 on point.mz (resolves Phase-1 mz_monotonic_data INFO)"
  - "MzPeakSpectrumWriter.OrderedPairs public helper (ascending + multiset-preserving emit)"
affects: [phase-03, phase-04]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Per-scan loop over [first,last] with MaxLevel/MsLevel gating; dense 0-based output ordinal"
    - "DUAL representation routed on scanEvent.ScanData==Profile && Scan.FromFile().HasCentroidStream"
    - "Full in-memory accumulation, one Parquet row group per facet (v1 prototype limitation)"

key-files:
  created:
    - ThermoRawFileParser/ThermoRawFileParserTest/MzPeakWriterTests.cs
  modified:
    - ThermoRawFileParser/Writer/MzPeakSpectrumWriter.cs

key-decisions:
  - "spectra_data is ALWAYS the as-acquired signal (ReadMZData centroid:false) for every in-range scan"
  - "spectra_peaks is written ONLY for profile scans with a CentroidStream; centroid-only scans never duplicated"
  - "OrderedPairs exposed public (not internal) so the test project can lock duplicate-m/z survival without InternalsVisibleTo"
  - "spectrum-set parity enforced by emitting one metadata spectrum row per data ordinal"

requirements-completed: [DATA-01, DATA-02, DATA-03, DATA-04]

# Metrics
duration: ~40min
completed: 2026-06-14
---

# Phase 2 Plan 01: Spectra Signal Data Summary

**`-f mzpeak` now emits the full point-layout signal for every in-range spectrum under DUAL
representation — `spectra_data` (as-acquired profile/centroid arrays) plus a conditional
`spectra_peaks` facet (Thermo CentroidStream label peaks for profile scans that carry them) — with a
per-ordinal `spectra_metadata` row set that covers the identical spectrum set, canonical widths
(mz f64 / intensity f32), non-decreasing m/z with the multiset preserved, honored filters, and
`sorting_rank:0` on `point.mz`.**

## What Was Built

- **spectra_data (Task 1):** Replaced the single-spectrum placeholder loop with a real per-scan loop
  over `[firstScanNumber, lastScanNumber]`. Each scan gets its level via
  `(int)raw.GetFilterForScanNumber(scanNumber).MSOrder`; scans with `level > MaxLevel` or
  `!MsLevel.Contains(level)` are skipped (zero rows, not counted). For each emitted scan,
  `ReadMZData(centroid:false)` yields the as-acquired arrays, which are appended to growing
  `spectrum_index` (dense 0-based `ulong` ordinal), `mz` (f64), and `intensity` (f32) accumulators.
- **spectra_metadata (Task 1):** `BuildMetadataFacet` now takes the collected per-ordinal lists and
  writes one `spectrum` row per emitted ordinal (`index=ordinal`, `id="index={ordinal}"`,
  `time=RetentionTimeFromScanNumber`). Its `spectrum_count` equals the `spectra_data` `spectrum_count`,
  so the two facets cover the identical spectrum set.
- **spectra_peaks (Task 2):** During the same loop, when
  `scanEvent.ScanData == ScanDataType.Profile && Scan.FromFile(raw, scanNumber).HasCentroidStream`, a
  SECOND `ReadMZData(centroid:true)` reads the Thermo CentroidStream label peaks into a parallel set of
  accumulators keyed on the SAME ordinal. The facet is written (and listed in `mzpeak_index.json` with
  `data_kind:"peaks"`) only when at least one peak row was accumulated. `BuildIndex(bool hasPeaks)`
  gates the index entry.
- **sorting + multiset:** `OrderedPairs` emits the Thermo-native ascending order and asserts
  non-decreasing; only on a detected violation does it apply a stable paired index sort
  (decorate-by-index, ties broken by original index). It never deduplicates or merges equal-m/z points.
- **sorting_rank:** `SpectrumArrayIndex` now declares `"sorting_rank":0` on the `point.mz` entry.
- **Tests (Task 3):** New `MzPeakWriterTests` (7 tests) lock ascending+multiset, f64/f32 leaf types,
  MS2-only filter exclusion, metadata/data spectrum-set parity (dense 0..N-1), peaks `data_kind`
  "peaks", and a non-tautological duplicate-m/z survival test driving `OrderedPairs` with
  `{100.0,100.0,100.0,200.0}` (asserts 4 rows survive in input order).

## Dual-Representation Routing (as implemented)

| Scan kind | `spectra_data` (centroid:false) | `spectra_peaks` (centroid:true) |
|-----------|---------------------------------|----------------------------------|
| Profile + HasCentroidStream | as-acquired profile arrays | Thermo CentroidStream label peaks |
| Profile, no CentroidStream | as-acquired profile arrays | — (not written) |
| Centroid-only (ScanData != Profile) | the centroid SegmentedScan | — (never duplicated) |

For `small.RAW`: 48 emitted spectra → 305,213 `spectra_data` points (matches the existing
`TestParquetProfile` profile total) and 12,890 `spectra_peaks` points across the profile scans that
carry a CentroidStream (well below the 48,520 centroid total in `TestParquetCentroid`, confirming
centroid-only scans are not routed to peaks).

## Verify-Gate Results

**Task 1 (DATA_OK):**
```
INFO Wrote mzPeak archive with 48 spectra (305213 data points)
DATA_OK 48 305213
```
(row count == point-count metadata; spectrum_count=48 > 1; dense 0-based spectrum_index; per-spectrum
m/z non-decreasing; metadata index dense 0..47; metadata count == data spectrum_count; sorting_rank:0
on point.mz.)

**Task 2 (PEAKS_OK):**
```
INFO Wrote mzPeak archive with 48 spectra (305213 data points, 12890 peak points)
PEAKS_OK 12890
```
(spectra_peaks present; data_kind "peaks"; mz f64 / intensity f32; non-decreasing; point-count
metadata == row count; spectrum_count == distinct spectrum_index count; index lists peaks iff written.)

**Task 3 (NUnit):**
```
Passed!  - Failed: 0, Passed: 7, Skipped: 0, Total: 7 - ThermoRawFileParserTest.dll (net8.0)
```

**Full regression:**
```
Passed!  - Failed: 0, Passed: 34, Skipped: 0, Total: 34 - ThermoRawFileParserTest.dll (net8.0)
```
(Phase 1 had 27 tests; +7 new MzPeakWriterTests; no existing test broken.)

## mzpeak-validate (DECISIVE GATE)

Baseline (Phase-1 archive, captured before this plan):
```
mzPeak validation: FAIL  (1 errors, 0 warnings)
  INFO    mz_monotonic_data            spectra_data:point.mz [recover:reorder_pair]
           spectra_data.point.mz: array index declares it unsorted (sorting_rank null/absent); monotonicity not enforced
  ERROR   columns_spectra_metadata     spectra_metadata
           spectra_metadata: required facet 'scan' absent
```

Phase-2 archive (fresh `small.RAW` conversion, this plan):
```
mzPeak validation: FAIL  (1 errors, 0 warnings)
  profile: mzpeak-0.9  catalog 1.6  CV {'MS': '4.1.254', 'IMS': '1.1.0', 'UO': '2026-01-16'}
  ERROR   columns_spectra_metadata     spectra_metadata
           spectra_metadata: required facet 'scan' absent
```

**Finding-list delta:**
- `mz_monotonic_data` INFO — **RESOLVED** by declaring `sorting_rank:0` on `point.mz` (no longer
  reported).
- `columns_spectra_metadata` ERROR (`required facet 'scan' absent`) — **PERSISTS**. The per-ordinal
  `spectrum` rows did NOT clear it: the validator requires the actual top-level `scan` facet
  (`source_index`, `scan_index`, scan_start_time, filter_string, scan_windows, ...), which is
  explicitly Phase-3 scope. Net result is one fewer finding than baseline; no NEW errors introduced.

No gate was weakened to accommodate this — the persisting ERROR is the planned Phase-3 `scan`-facet
item and is reported verbatim.

## Deviations from Plan

### Gate-command mechanics (no production/gate change)

**1. [Rule 3 - Blocking] `dotnet build -c Release` from the git root has no project/solution to build**
- **Found during:** Task 3 verify.
- **Issue:** The verify command runs `dotnet build -c Release` with the repo root as cwd, but the
  solution lives at `ThermoRawFileParser/ThermoRawFileParser.sln`; MSBuild errored with
  `MSB1003: Specify a project or solution file`.
- **Fix:** Built the solution explicitly (`dotnet build -c Release ThermoRawFileParser/ThermoRawFileParser.sln`)
  and ran the test filter unchanged. The substantive intent (full build incl. tests, then run
  `MzPeakWriterTests`) is met. No writer/test logic changed; same class of gate-command mechanics noted
  in the Phase-1 SUMMARY.

### Implementation choice

**2. [Plan-permitted] `OrderedPairs` exposed `public static` rather than `internal`**
- The plan allows extracting the emit step into "a small testable internal helper if needed". The test
  project has no `InternalsVisibleTo`, so `internal` would not be visible; `public static` is the
  least-invasive way to let Task-3 (f) drive the real emit path with deterministic duplicate-m/z input.
  This adds one pure, side-effect-free helper to the public surface; no behavior change.

**Total deviations:** 2 (1 gate-command path correction, 1 plan-permitted visibility choice).
**Impact:** No scope creep, no production logic weakened, no gate weakened.

## v1 Prototype Limitation (confirmed)

v1 uses **full in-memory accumulation** of all point/peak/metadata arrays and writes a **single Parquet
row group per facet**. No large-RAW-safe claim is made. Row-group batching (the existing
`ParquetSpectrumWriter` flushes at ~1M rows so all ions of a scan stay in one group) is deferred as a
**v2/OPT** follow-up.

## What Phase 3 Needs

- **The `scan` facet** (and `precursor` / `selected_ion`) in `spectra_metadata` — this is the one
  remaining `mzpeak-validate` ERROR (`required facet 'scan' absent`). Phase 3 must add the top-level
  `scan` struct (source_index/scan_index/scan_start_time/filter_string/scan_windows/...) referencing
  back to the spectrum ordinals already emitted here.
- **Rich `spectrum` metadata** beyond the honest index/id/time minimum: ms_level, polarity,
  representation, lowest/highest observed mz, number_of_data_points, number_of_peaks, base peak,
  TIC, parameters (use the `MzPeakParquet.BuildParamField` PARAM shape and the ground-truth
  `spectrum_array_index` entries).
- The dense 0-based ordinal contract (`spectrum_index`) and the per-ordinal metadata row set are the
  join key Phase 3 should reference via `source_index`.
- VER-02 facet equivalence vs mzML2mzPeak (profile-mode differential) remains Phase 4.

## Self-Check: PASSED

- Created file verified present: `ThermoRawFileParser/ThermoRawFileParserTest/MzPeakWriterTests.cs`
- Modified file verified present: `ThermoRawFileParser/Writer/MzPeakSpectrumWriter.cs`
- Task commits verified in git log: 289edf2 (Task 1), 1e3b644 (Task 2), 18902a4 (Task 3)

---
*Phase: 02-spectra-signal-data*
*Completed: 2026-06-14*
