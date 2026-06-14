---
phase: 03-spectra-metadata-file-level-metadata-index
plan: 01
subsystem: writer
tags: [mzpeak, parquet, metadata, controlled-vocabulary, ontology-mapping, validator, thermo]

# Dependency graph
requires:
  - phase: 02-spectra-signal-data
    provides: per-ordinal scan loop, OrderedPairs, point-facet writer, ScanTrailer, centroid/profile routing
provides:
  - spectra_metadata.parquet with four co-resident struct tables (spectrum, scan, precursor, selected_ion) linked by source_index
  - general nested def/rep-level computer + list-of-struct write path in MzPeakParquet
  - mzpeak_index.json metadata{} block + spectra_metadata footer KV with generated cv_list, instrument_configuration_list, software_list, data_processing_method_list, file_description, run
  - mzpeak-validate PASS (0 errors, 0 warnings) on small.RAW conversion
affects: [04-chromatograms, mzpeak-fidelity, verify-work]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Four independent right-padded struct tables in one Parquet file, linked by source_index (NOT row position)"
    - "Generated cv_list collected from emitted CvColumn/PARAM prefixes, written to both index and footer KV"
    - "File-level metadata built once as JObject, serialized verbatim into footer KV and cloned into the index"

key-files:
  created: []
  modified:
    - ThermoRawFileParser/Writer/MzPeakSpectrumWriter.cs
    - ThermoRawFileParser/Writer/MzPeak/MzPeakParquet.cs
    - ThermoRawFileParser/ThermoRawFileParserTest/MzPeakWriterTests.cs
    - ThermoRawFileParser/ThermoRawFileParserTest/MzPeakParquetTests.cs

key-decisions:
  - "cv_list generated from the collected CV-prefix set (asserted coverage), not hard-coded {MS,UO}"
  - "Reused existing Param data class for column data; added JParam/CvParam JSON helpers rather than a second Param member"
  - "Tracked ionization mode per analyzer config (parallel to the distinct-analyzer map) for ionsource/analyzer/detector component emission via OntologyMapping"
  - "Emitted metadata blocks into BOTH index metadata{} and the parquet footer KV (validator reads cv_list from the index, schema-checks the footer blocks)"

patterns-established:
  - "CvParam/JParam: param.json-conformant objects (name required; accession/value/unit nullable) that also record CV prefixes for cv_list generation"
  - "Per-config instrument metadata from OntologyMapping (UpdateFTMSDefinition + GetInstrumentModel + IonizationTypes + MassAnalyzerTypes + GetDetectors)"

requirements-completed: [META-01, META-02, META-03, META-04, META-05, IDX-01, IDX-02, IDX-03, IDX-04]

# Metrics
duration: 35min
completed: 2026-06-14
---

# Phase 3 Plan 01: Spectra Metadata + File-Level Metadata/Index Summary

**Completed the rich four-table `spectra_metadata.parquet` and the file-level metadata (generated cv_list, instrument/software/data-processing/file-description blocks) in both the index and the Parquet footer, taking `mzpeak-validate` on a small.RAW conversion from one outstanding error to a clean PASS with zero errors and zero warnings.**

## Performance

- **Duration:** ~35 min
- **Started:** 2026-06-14T12:05:00Z
- **Completed:** 2026-06-14T12:17:00Z
- **Tasks executed this run:** 2 (Task 4 + Task 5; Tasks 1-3 were already committed)
- **Files modified this run:** 2 (MzPeakSpectrumWriter.cs, MzPeakWriterTests.cs)

## Accomplishments

- Task 4: emitted the full `metadata{}` block into `mzpeak_index.json` AND the `spectra_metadata.parquet` footer KV — a GENERATED `cv_list` (collected from every emitted CvColumn/PARAM CV prefix, covering exactly {MS, UO} for small.RAW), `instrument_configuration_list` (two configs: FTMS id0, ion trap id1, each ionsource/analyzer/detector via OntologyMapping), `software_list`, `data_processing_method_list`, `file_description`, `run`, and empty `sample_list`/`scan_settings_list`.
- Task 5: added NUnit locks for the canonical `.../list/item/...` leaf paths, CV values (selected_ion_mz positive, charge_state null on small.RAW, isolation_window_target_mz with no unit suffix), per-ordinal `number_of_peaks` cross-checked against the actual `spectra_peaks` facet, plus the decisive end-to-end validator gate parsing findings by error id.
- Decisive validator gate: `mzpeak-validate small.mzpeak` returns **PASS (0 errors, 0 warnings)** — `columns_spectra_metadata` (scan-facet) and `cv_list_declared` are both absent, and no new error id appears.

## Task Commits

1. **Task 4: index metadata{} + footer cv_list + file-level metadata blocks** - `9ef8cb3` (feat)
2. **Task 5: canonical list/item leaf-path + CV-value locks + per-ordinal peak-count gate** - `d9118a8` (test)

Tasks 1-3 (pre-existing, committed before this run):
- Task 1: general nested def/rep-level computer with pyarrow proof - `7e0d0b8` (feat)
- Task 2: rich spectrum + required scan facet as co-resident tables - `885ad39` (feat)
- Task 3: precursor + selected_ion MSn facets, parent-linked by source_index - `e19ed34` (feat)

## Files Created/Modified

- `ThermoRawFileParser/Writer/MzPeakSpectrumWriter.cs` - Added `using ThermoRawFileParser.Writer.MzML` (for `CVParamType`); per-config ionization tracking (`_ionizationOrder`, `AnalyzerIndex` now takes ionization); `AddMetadataBlocks` now builds the full metadata JObject and writes every block into the footer KV; `BuildCvList`/`BuildInstrumentConfigurations`/`BuildSoftwareList`/`BuildDataProcessingList`/`BuildFileDescription`/`BuildRun`/`Component`/`CvParam`/`JParam` helpers; `BuildIndex` now embeds the shared `_metadataBlocks` clone.
- `ThermoRawFileParser/ThermoRawFileParserTest/MzPeakWriterTests.cs` - Added cv_list coverage lock (index + footer), file-level block shape lock, the validator gate (parse by error id, allowlist subset), canonical list/item leaf-path + CV-value lock, and per-ordinal number_of_peaks gate.

## Validator Finding List (decisive gate)

Command: `mzpeak-validate /tmp/p3final.mzpeak`

```
mzPeak validation: PASS  (0 errors, 0 warnings)
  archive: /tmp/p3final.mzpeak
  profile: mzpeak-0.9  catalog 1.6  CV {'MS': '4.1.254', 'IMS': '1.1.0', 'UO': '2026-01-16'}
```

JSON report (`--json`): `verdict: PASS`, `summary: {"errors": 0, "warnings": 0}`, `findings: []`.

Progression across this plan (small.RAW conversion, x64 Release DLL):
- Before Tasks 1-3: `ERROR columns_spectra_metadata` (required facet 'scan' absent).
- After Tasks 1-3 (clean base for this run): `columns_spectra_metadata` GONE; one remaining `ERROR cv_list_declared` (archive uses CV codes ['MS', 'UO'] but metadata.cv_list absent).
- After Task 4: `cv_list_declared` GONE -> **PASS, 0 errors, 0 warnings**.

The pre-existing reference-archive failures (`index_schema_valid:metadata`, `cv_list_declared`) that the official `refs/mzPeak/small.mzpeak` still exhibits are NOT present in our output: we emit `metadata.version` and a generated `cv_list`. No new ERROR id was introduced.

## Deviations from Plan

### Build/runtime path correction (not a code deviation)

The convert command in the orchestration prompt pointed at `ThermoRawFileParser/bin/Release/net8.0/ThermoRawFileParser.dll`, but the solution builds the x64 platform to `ThermoRawFileParser/bin/x64/Release/net8.0/`. The `bin/Release/net8.0/` DLL was stale (an older thin-metadata build), which initially masked the four-facet output. All conversions and the validator gate were run against the correct freshly-built `bin/x64/Release/net8.0/ThermoRawFileParser.dll`. No source change was needed.

### Compile-trap avoidance (as instructed)

- `CVParamType` resolved via `using ThermoRawFileParser.Writer.MzML;` (its home namespace), used only to read `.accession`/`.name`/`.value` from OntologyMapping outputs.
- No second `Param` member: the existing private `Param` data class (used by `Record` for column data) was kept; the JSON-building method is named `JParam` to avoid the CS0102 collision the prior aborted attempt hit (confirmed by a deliberate build that reproduced and then fixed the error).

## Known Stubs

None. `sample_list`/`scan_settings_list` are intentionally empty arrays (TRFP has no sample metadata; valid per the reference shape and validator schema). `spectrum.parameters` is currently emitted as an empty list per row (the optional scan-param PARAMs in RESEARCH Decision 3 are fidelity-only and not required by the validator); this is the same shape the reference accepts and does not block the plan goal.

## Self-Check: PASSED

- `ThermoRawFileParser/Writer/MzPeakSpectrumWriter.cs` - FOUND
- `ThermoRawFileParser/ThermoRawFileParserTest/MzPeakWriterTests.cs` - FOUND
- Commit `9ef8cb3` - FOUND
- Commit `d9118a8` - FOUND
- Full test suite: 44 passed, 0 failed, 0 skipped
- Validator gate: PASS (0 errors, 0 warnings)
