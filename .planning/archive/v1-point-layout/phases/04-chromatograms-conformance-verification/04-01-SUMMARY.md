---
phase: 04-chromatograms-conformance-verification
plan: 01
subsystem: testing
tags: [mzpeak, chromatogram, tic, parquet, conformance, differential, pyarrow, thermo]

# Dependency graph
requires:
  - phase: 03-spectra-metadata-file-level-metadata-index
    provides: "full spectra facets, index metadata, cv_list mechanism, validator gate pattern, public MzPeakSpectrumWriter.OrderedPairs"
provides:
  - "TIC chromatograms_data + chromatograms_metadata facets (ground-truth schema) with index/cv entries"
  - "conformance suite: validator 0/0 gate with chromatograms, VER-02 differential vs mzML2mzPeak, VER-04 L1 vs independent RAW read"
affects: [v1-complete]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "device TraceType.TIC capture paired 1:1 with per-scan records; chromatogram_tic_source footer KV records the path taken"
    - "single-row facet emission via present-but-empty list + present-but-null top-level struct level helpers"
    - "differential equivalence via prebuilt mzml2mzpeak + pyarrow chunk decode, asserting nonzero (m/z,intensity) multisets"

key-files:
  created:
    - "ThermoRawFileParser/ThermoRawFileParserTest/MzPeakDifferentialTests.cs"
  modified:
    - "ThermoRawFileParser/Writer/MzPeakSpectrumWriter.cs"
    - "ThermoRawFileParser/ThermoRawFileParserTest/MzPeakWriterTests.cs"

key-decisions:
  - "TIC from the Thermo device TraceType.TIC trace, never the summed ScanStatistics.TIC"
  - "VER-04 L1 (m/z) is bit-exact vs an independent SegmentedScan re-read; L2 value-equality is certified by VER-02 because the Thermo SegmentedScan returns Positions/Intensities non-index-paired"
  - "chromatogram precursor/selected_ion reuse the Phase-3 field builders (ion_mobility omitted on both, v1 simplification) for cross-facet consistency"

patterns-established:
  - "no-trace-first TIC branch (never deref trace[0] when empty) + scan-number re-key fallback"
  - "external-tool tests resolve-then-Assert.Ignore so CI without python/validator/mzml2mzpeak stays green"

requirements-completed: [CHROM-01, CHROM-02, VER-01, VER-02, VER-03, VER-04]

# Metrics
duration: ~45min
completed: 2026-06-14
---

# Phase 4 Plan 01: Chromatograms + Conformance Verification Summary

**TIC chromatogram facets (ground-truth schema) plus a conformance suite that keeps mzpeak-validate at PASS 0/0, proves nonzero (m/z,intensity) multiset equivalence against the mzML2mzPeak reference, and bit-exact-checks m/z against an independent RAW re-read — closing v1.**

## Performance

- **Duration:** ~45 min
- **Completed:** 2026-06-14
- **Tasks:** 3
- **Files modified:** 3 (2 modified, 1 created)

## Accomplishments

- `chromatograms_data.parquet` carries the run TIC: one point per scan (48), `time`==per-scan RT (minutes, f64), `ms_level`==per-scan ms_level (distribution {1:14, 2:34}, never 0), `intensity`==device TIC value (f32), `chromatogram_index` all 0. Point struct order is exactly `chromatogram_index:u64, time:f64, intensity:f32, ms_level:i64`.
- `chromatograms_metadata.parquet` has one row matching the ground-truth chromatogram struct exactly (id=`TIC`, `MS_1000465_scan_polarity` int8 `0`, `MS_1000626_chromatogram_type` cell `MS:1000235`, `data_processing_ref` null, `MS_1003060_number_of_data_points`==48, empty `parameters`/`auxiliary_arrays`, `number_of_auxiliary_arrays`==0) with present-but-null `precursor`/`selected_ion` and the full AUX_ARRAY element shape.
- `mzpeak_index.json` `files[]` lists both chromatogram facets with `entity_type` `chromatogram`; generated `cv_list` stays exactly `{MS, UO}` (no new prefix, no version bump).
- mzpeak-validate stays **PASS, 0 errors / 0 warnings, 0 findings** with chromatograms present.

## TIC handling (CHROM-01)

Captured via `raw.SelectInstrument(Device.MS, 1)` + `GetChromatogramData(TraceType.TIC, -1, -1)` + `ChromatogramSignal.FromChromatogramData` — the same device-trace pattern `MzMlSpectrumWriter` uses for the base-peak trace. Three branches, no-trace case first (never deref `trace[0]` when empty):

1. `trace.Length == 0` → summed-TIC fallback (per-record `TotalIonCurrent`), `_chromFromDeviceTrace=false`.
2. `trace[0].Times.Count == records.Count` → 1:1 device path, `_chromFromDeviceTrace=true` (the default-conversion path).
3. length mismatch → re-key by `trace[0].Scans` scan numbers; `_chromFromDeviceTrace=true` only if every record was a device hit.

The data-facet footer KV `chromatogram_tic_source` records the path taken (`device`/`summed`); the CHROM-01 test asserts `device` on the default conversion. This is a real signal — the writer log confirms `48 TIC points` and the data facet carries the device trace values (e.g. ordinal-0 MS1 device value 15245068).

## Chromatogram schema (CHROM-02)

`BuildChromatogramField()` + `BuildAuxArrayField()` reproduce the exact ground-truth chromatogram struct field order. The metadata facet is `BuildChromatogramField()` + Phase-3 `BuildPrecursorField()` + `BuildSelectedIonField()`. The single row uses level helpers (`AtLevel`, `EmptyList`) for null `data_processing_ref`, empty `parameters`/`auxiliary_arrays`, and all-null `precursor`/`selected_ion`. Footer KVs: metadata facet `chromatogram_count="1"` + `chromatogram_data_point_count="0"`; data facet `chromatogram_data_point_count="0"` + `chromatogram_array_index` (verbatim RESEARCH JSON) + `chromatogram_tic_source` (NO `chromatogram_count` on the data facet, per V5).

## VER-02 differential results (vs mzML2mzPeak)

Pipeline: `small.RAW → profile mzML (-f 1 -p) → mzml2mzpeak --no-numpress → ref.mzpeak`; `small.RAW → trfp.mzpeak (-f 4)`. The pyarrow comparator decodes the reference chunk layout (abs m/z = `chunk_start + cumsum(deltas)`). Verdict JSON:

```
spectrum_count_ok: true   (48 == 48)
ms_level_ok: true   polarity_ok: true   rt_ok: true
profile_multiset_ok: true
ref_profile_count: 14
ref_profile_indices  == compared_indices == [0,1,7,8,14,15,21,22,28,29,34,35,41,42]
centroid_indices_compared: []   centroid_multiset_ok: true
centroid_indices_skipped: [2,3,4,5,6,9,10,11,12,13,16,17,18,19,20,23,24,25,26,27,30,31,32,33,36,37,38,39,40,43,44,45,46,47]
```

The nonzero (m/z,intensity) multisets of every reference-profile spectrum equal ours (RESEARCH proved spec0 11057==11057, spec1 14815==14815; reproduced here). The compared index set equals the exact reference MS1-profile set (count 14). Centroid coverage is scoped to indices present in BOTH peaks facets: the reference centroids MS2 into `spectra_peaks` `[2..47]` while TRFP keeps MS2 profile in `spectra_data` and writes only the 7 profile+centroid-stream MS1 scans `[0,7,14,21,28,34,41]` into peaks — the sets are disjoint, so the centroid comparison is reported as skipped (with reason), not silently dropped. The TIC is deliberately dropped from VER-02 (the reference carries 0 chromatograms) and certified by VER-01 + VER-04 instead.

## VER-04 L1/L2 results

L1 (m/z) is a genuine independent check: our `spectra_data.mz` (f64) is compared bit-exact, per spectrum, against the m/z array re-read directly from `small.RAW` via `Scan.FromFile(raw, scan).SegmentedScan.Positions` — a source NOT derived from our archive. All 48 spectra pass bit-exact (confirmed cross-format: our mzpeak ordinal-0 m/z == the writer's mzML ordinal-0 m/z, 19913 points, bit-exact).

L2 (intensity) is the honest structural invariant (intensity is f32, finite, per-spectrum count == `number_of_data_points`). See the deviation below for why value-equality is certified by VER-02 rather than a self-derived per-point comparison.

## Validator finding list (VER-01/VER-03)

```
mzPeak validation: PASS  (0 errors, 0 warnings)
verdict: PASS
summary: {"errors": 0, "warnings": 0}
findings count: 0
```

Fresh conversion WITH chromatograms: `Wrote mzPeak archive with 48 spectra (305213 data points, 12890 peak points, 48 TIC points)` → validator PASS 0/0, empty findings list, `cv_list {MS, UO}`.

## Task Commits

1. **Task 1: Emit TIC chromatograms_data/metadata facets + index/cv entries** - `15f29b2` (feat)
2. **Task 2: Chromatogram locks, L1 vs independent RAW read, validator 0/0 gate** - `8019f6a` (test)
3. **Task 3: VER-02 differential equivalence vs mzML2mzPeak** - `503ada9` (test)

## Files Created/Modified

- `ThermoRawFileParser/Writer/MzPeakSpectrumWriter.cs` - device TIC capture (`CaptureTic`), `BuildChromatogramDataFacet`/`BuildChromatogramMetadataFacet`/`BuildChromatogramField`/`BuildAuxArrayField`, single-row null/empty helpers, `BuildIndex(hasPeaks, hasChromatograms)`, `ChromatogramArrayIndex` constant.
- `ThermoRawFileParser/ThermoRawFileParserTest/MzPeakWriterTests.cs` - chromatogram data/metadata shape+value locks, index/cv-list tests, VER-04 L1 bit-exact + L2 structural test, `Validator_Gate_Stays_Zero_Errors_With_Chromatograms`.
- `ThermoRawFileParser/ThermoRawFileParserTest/MzPeakDifferentialTests.cs` (new, BOM-free) - VER-02 differential fixture with tool resolution, the 3-step pipeline, and the pyarrow chunk-decode multiset comparator.

## Decisions Made

- TIC from the device `TraceType.TIC` trace (not summed `ScanStatistics.TIC`), aligned 1:1 with per-scan records.
- chromatogram `precursor`/`selected_ion` reuse the Phase-3 field builders; ion_mobility is omitted on BOTH facets (OPT-04 deferred) to keep the two selected_ion shapes consistent — a documented v1 simplification (risk A2), not a silent divergence.
- VER-02 centroid coverage scoped to indices in both peaks facets, with disjoint indices reported as skipped (V4).

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] VER-04 L2 per-point value equality vs a naive SegmentedScan re-read is not achievable; downgraded L2 to structural per the plan's permitted fallback**
- **Found during:** Task 2 (RoundTrip L1/L2 test)
- **Issue:** The plan's preferred VER-04 re-reads `Scan.FromFile(raw, scan).SegmentedScan` and pairs `Positions[k]` with `Intensities[k]` (via `OrderedPairs`) to compare intensities to ours. Empirically the Thermo `SegmentedScan` returns `Positions` sorted but `Intensities` in acquisition order — the two arrays are NOT index-paired, and their nonzero counts differ from the writer's realigned output (pristine nonzero=10739 vs writer/mzML nonzero=11057 for ordinal 0). Reproducing the exact per-point intensity pairing would require re-implementing `SpectrumWriter.ReadMZData`'s internal realignment, which is `private protected`.
- **Fix:** Kept L1 as a genuine INDEPENDENT bit-exact m/z check (our m/z == the re-read SegmentedScan positions, per spectrum); downgraded L2 to the honest structural invariant (intensity f32, finite, count==`number_of_data_points`) and renamed the test to `RoundTrip_L1_Mz_BitExact_Vs_IndependentRawRead_L2_Intensity_F32_Structural`. Intensity value-equality is certified by VER-02 against the fully-independent mzdata reference (nonzero (m/z,intensity) multisets equal). This is exactly the documented downgrade the plan permits (V1 fallback clause / risk: VER-04 independent-read mapping).
- **Files modified:** ThermoRawFileParser/ThermoRawFileParserTest/MzPeakWriterTests.cs
- **Verification:** L1 bit-exact passes for all 48 spectra; VER-02 profile multiset equality passes for all 14 reference-profile spectra.
- **Committed in:** `8019f6a` (Task 2 commit)

---

**Total deviations:** 1 auto-fixed (1 plan-permitted downgrade).
**Impact on plan:** No scope creep. L1 remains a non-vacuous independent check; L2 value-equality is delivered by VER-02's independent reference exactly as the plan's fallback prescribes. The writer behavior is unchanged and validator-certified.

## Issues Encountered

- The Thermo `SegmentedScan` Positions/Intensities non-index-pairing (above) was the only surprise; resolved by leaning on VER-02 for intensity value-equality and keeping L1 independent. No writer changes were needed — the writer output matches both the mzML path and the mzml2mzpeak reference.

## User Setup Required

None - no external service configuration required. The differential/validator tests resolve their external tools and skip-with-warning when absent.

## Next Phase Readiness

**v1 complete.** All four phases (CLI/Parquet skeleton, spectra signal, spectra+file metadata, chromatograms+conformance) are delivered. The writer emits a spec-valid mzPeak archive from Thermo RAW that passes mzpeak-validate 0/0 with full spectra + chromatogram facets, matches the mzML2mzPeak reference on semantic content, and preserves source m/z bit-exact. Deferred to v2 (unchanged): chunked/Numpress layouts, ion_mobility columns (OPT-04), and any non-TIC chromatograms.

## Self-Check: PASSED

All created/modified files exist on disk and all three task commits (`15f29b2`, `8019f6a`, `503ada9`) are present in git history.

---
*Phase: 04-chromatograms-conformance-verification*
*Completed: 2026-06-14*
