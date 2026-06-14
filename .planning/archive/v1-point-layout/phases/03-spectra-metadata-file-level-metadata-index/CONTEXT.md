# Phase 3 Context: Spectra Metadata + File-Level Metadata/Index

**Requirements:** META-01..05, IDX-01..04
**Depends on:** Phase 2 (signal facets + per-ordinal `spectrum` rows + the dense 0-based ordinal join key).

## Intent

Turn the minimal `spectra_metadata.parquet` (currently just a thin `spectrum` struct) into the full
packed-parallel-table set, and populate the `mzpeak_index.json` `metadata{}` block. This clears the
last expected validator error (`scan` facet absent) and should bring the archive to a clean/near-clean
`mzpeak-validate` pass for non-chromatogram content.

## Decisions (locked)

- **Packed parallel tables** in one `spectra_metadata.parquet` = four nullable TOP-LEVEL struct columns
  (`spectrum`, `scan`, `precursor`, `selected_ion`). Each ROW populates exactly one struct; the other
  three are null (definition-level discipline via the proven `MzPeakParquet` helper). `scan`/`precursor`/
  `selected_ion` link back to their spectrum via `source_index` = the spectrum's dense ordinal.
- **Row ordering:** match the ground truth — for the run, emit all `spectrum` rows, then all `scan` rows,
  then `precursor`, then `selected_ion` (or interleaved per the reference; follow what the reference
  archive `refs/mzPeak/small.unpacked.mzpeak/spectra_metadata.parquet` actually does — verify with pyarrow).
- **Rich `spectrum` fields** (per ground truth, CV-accession column names): `MS_1000511_ms_level` (uint8),
  `MS_1000465_scan_polarity` (int8 +1/-1), `MS_1000525_spectrum_representation` (CURIE MS:1000127 centroid /
  MS:1000128 profile), `MS_1000559_spectrum_type` (CURIE MS:1000579 MS1 / MS:1000580 MSn), observed mz
  range (lowest/highest), number_of_data_points, number_of_peaks, base_peak_mz, base_peak_intensity,
  total_ion_current, plus the `parameters` PARAM list. Keep `index/id/time` from Phase 2.
- **`scan`:** `MS_1000016_scan_start_time` (unit UO_0000031 minute — note Thermo RetentionTime is minutes),
  `MS_1000512_filter_string`, `MS_1000927_ion_injection_time`, `instrument_configuration_ref`, scan_windows
  (lower/upper), `source_index`.
- **`precursor`** (MSn only): isolation_window (target mz / lower offset / upper offset), activation
  (PARAM list: dissociation method CURIE + collision energy), `precursor_id`, `source_index`.
- **`selected_ion`** (MSn only): `MS_1000744_selected_ion_mz`, `MS_1000041_charge_state`,
  `MS_1000042_intensity`, `source_index`.
- **`mzpeak_index.json` metadata{}** block: `instrument_configuration_list` (ionsource/analyzer/detector
  components with CV params, derived from Thermo instrument model via `OntologyMapping`), `software_list`
  (a ThermoRawFileParser entry), `data_processing_method_list` (conversion step + the intensity-narrowing /
  sort provenance), `file_description` (source RAW file + contents params), `cv_list` (MS, UO — a SUPERSET
  of every accession actually used), `sample_list`/`scan_settings_list` (may be empty arrays, match reference).
- **CV mapping:** use `refs/_findings/mzpeak_mapping_report.md` + the ground-truth column names as the
  authority. REUSE the existing `OntologyMapping` dictionaries and the extraction logic already in
  `MzMlSpectrumWriter` (precursor/isolation/activation/scan-trailer) — do NOT re-derive Thermo decoding.

## Reuse (do NOT reinvent)

- `MzPeakParquet` helper (BuildParamField/Column/WriteAsync/CvColumn) from Phase 1 — extend, don't fork.
- `MzMlSpectrumWriter` already extracts: scan filter string, ion injection time (scan trailer),
  precursor m/z, isolation window, activation/collision energy, charge, monoisotopic m/z. Mine those
  exact code paths (the research pass will map them).
- `OntologyMapping` (instrument models, analyzers, ionization, dissociation) + `MetadataWriter` (how
  instrument/file metadata is already assembled for the JSON/TXT metadata outputs).

## Constraints / runtime

- m/z=f64, intensity=f32; CURIE column-name convention via `CvColumn`; CV values as `NS:accession`.
- `cv_list` MUST contain every accession referenced (validator resolves CURIEs against it).
- Build: roll-forward env; run/test via `~/.dotnet-x64` Rosetta x64 runtime (DOTNET_ROOT_X64). Validator: `mzpeak-validate`.
- Compact code; explicit usings; BOM-free new files; NO comments referencing harness/process/phases.

## Verification (this phase)

- Build + full NUnit green (add tests: per-row null discipline across the 4 structs; source_index linkage;
  CV column names/values; MSn rows present for MS2 scans; instrument_config/cv_list shape).
- `mzpeak-validate small.mzpeak`: the `scan`-facet error is GONE; report any remaining findings (aim for a
  clean pass on spectra; chromatograms are Phase 4).
- Spot-diff our `spectra_metadata` against `refs/mzPeak/small.unpacked.mzpeak/spectra_metadata.parquet`
  (column names, struct shapes, CURIE forms) with pyarrow.

## Reference artifacts

- `refs/_findings/mzpeak_groundtruth_schema.md` (exact schema), `refs/_findings/mzpeak_mapping_report.md` (CV bible).
- `refs/mzPeak/small.unpacked.mzpeak/` (real metadata facet + index to diff).
- `.planning/phases/02-.../02-01-SUMMARY.md` (current writer/helper state, ordinal join key).
- `ThermoRawFileParser/Writer/MzMlSpectrumWriter.cs`, `OntologyMapping.cs`, `MetadataWriter.cs`, `ScanTrailer.cs`, `PrecursorInfo.cs`.
