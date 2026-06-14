# Requirements: mzPeak Writer for ThermoRawFileParser

**Defined:** 2026-06-14
**Core Value:** Produce a spec-valid mzPeak archive readable by the reference readers, straight from Thermo RAW, without losing spectral information.

## v1 Requirements

### CLI Integration

- [ ] **CLI-01**: `MzPeak` added to `OutputFormat` enum and selectable via `-f mzpeak` / `--format mzpeak`
- [ ] **CLI-02**: `--format` help text lists mzPeak; output filename gets `.mzpeak` extension
- [ ] **CLI-03**: `RawFileParser` dispatch instantiates `MzPeakSpectrumWriter` for the new format

### Parquet Foundation

- [ ] **PQ-01**: Write a Parquet file with nested structs + lists-of-structs using Parquet.Net (spike-validated approach)
- [ ] **PQ-02**: Reusable helpers to build the repeated `PARAM` value-struct and CV-accession-named columns
- [ ] **PQ-03**: Per-Parquet ZSTD compression; assemble facets into a STORED (uncompressed) ZIP archive

### Spectra Data

- [ ] **DATA-01**: `spectra_data.parquet` in point layout `struct<spectrum_index:u64, mz:f64, intensity:f32>`
- [ ] **DATA-02**: m/z coerced to float64, intensity to float32; m/z sorted ascending per spectrum (multiset preserved)
- [ ] **DATA-03**: Centroid peaks written to `spectra_peaks.parquet` when a centroid representation exists
- [ ] **DATA-04**: MS-level / scan-range filtering honored (reuse `ParseInput` filters)

### Spectra Metadata (packed parallel tables)

- [ ] **META-01**: `spectrum` struct rows — index, id, ms_level, time, polarity, representation, type, observed m/z range, counts, base peak, TIC
- [ ] **META-02**: `scan` struct rows — scan_start_time (minutes), filter_string, ion_injection_time, instrument_configuration_ref, scan_windows
- [ ] **META-03**: `precursor` struct rows — isolation window (target/lower/upper offsets) + activation params
- [ ] **META-04**: `selected_ion` struct rows — selected ion m/z, charge state, intensity
- [ ] **META-05**: CV terms encoded as CURIEs via the established mzML-CV→mzPeak mapping table

### File-Level Metadata & Index

- [ ] **IDX-01**: `mzpeak_index.json` lists all present facets (`files[]`) with entity_type/data_kind
- [ ] **IDX-02**: `metadata{}` block with instrument_configuration_list (ionsource/analyzer/detector + CV params) from Thermo instrument info via `OntologyMapping`
- [ ] **IDX-03**: software_list (TRFP entry) and data_processing_method_list (conversion + any narrowing/sort provenance)
- [ ] **IDX-04**: file_description (source RAW file, contents params) and cv_list (MS, UO)

### Chromatograms

- [ ] **CHROM-01**: `chromatograms_data.parquet` point layout `struct<chromatogram_index:u64, time:f64, intensity:f32, ms_level:i64>` with the TIC
- [ ] **CHROM-02**: `chromatograms_metadata.parquet` with the chromatogram struct (type=TIC CURIE, polarity, point count)

### Conformance & Verification

- [ ] **VER-01**: Output archive opens in the reference Python mzPeak reader without error
- [ ] **VER-02**: Spectrum count, per-spectrum peak counts, and (m/z,intensity) multiset round-trip vs. the source (within f32 intensity tolerance)
- [ ] **VER-03**: NUnit test converts `ThermoRawFileParserTest/Data/small.RAW` to mzPeak and asserts archive structure + readability

## v2 Requirements

### Optimizations

- **OPT-01**: Chunked layout with delta-encoded m/z chunks
- **OPT-02**: Numpress (SLOF/linear) intensity/m/z compression
- **OPT-03**: Null-marking / zero-run stripping for profile data
- **OPT-04**: Ion-mobility (FAIMS) value + type columns populated
- **OPT-05**: Profile + centroid dual emission (`spectra_data` profile, `spectra_peaks` centroid) for the same scan

## Out of Scope

| Feature | Reason |
|---------|--------|
| imzML / imaging spatial extension | Not applicable to Thermo LC-MS; reference imaging path ignored |
| mzPeak → RAW/mzML reverse conversion | TRFP is one-directional |
| Cloud/S3 streaming output | Existing TRFP S3 path not in scope for the prototype |
| Full data-transform provenance graph | Minimal provenance only for v1 |

## Traceability

| Requirement | Phase | Status |
|-------------|-------|--------|
| CLI-01..03 | Phase 1 | Pending |
| PQ-01..03 | Phase 1 | Pending |
| DATA-01..04 | Phase 2 | Pending |
| META-01..05 | Phase 3 | Pending |
| IDX-01..04 | Phase 3 | Pending |
| CHROM-01..02 | Phase 4 | Pending |
| VER-01..03 | Phase 4 | Pending |

**Coverage:**
- v1 requirements: 24 total
- Mapped to phases: 24
- Unmapped: 0

---
*Requirements defined: 2026-06-14*
*Last updated: 2026-06-14 after initialization*
