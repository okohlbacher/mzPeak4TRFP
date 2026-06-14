# Roadmap: mzPeak Writer for ThermoRawFileParser

## Overview

This roadmap takes ThermoRawFileParser from "no mzPeak support" to "emits a spec-valid
mzPeak archive readable by the reference Python/Rust readers, straight from Thermo RAW."
It uses a vertical-slice / walking-skeleton strategy: Phase 1 wires the new `mzpeak`
format end-to-end through the CLI and proves the single biggest unknown — that Parquet.Net
v5.0.1 can express nested structs, lists-of-structs, and parallel nullable top-level
columns via its low-level `ParquetSchema`/`DataColumn` API. Only once a (minimal) archive
opens in the reference reader do later phases pile on fidelity: spectra signal data
(Phase 2), spectra + file-level metadata (Phase 3), and chromatograms + conformance
round-trip (Phase 4). Each phase is independently verifiable and unblocks the next.

**Process note (not a deliverable):** This project mandates an external adversarial code
review using the `codex` and `vibe` CLIs at the START (plan) and CLOSE (certification) of
every phase. This is a quality gate around each phase, not work inside any phase, and must
never appear in the produced code or output schema.

## Phases

**Phase Numbering:**
- Integer phases (1, 2, 3): Planned milestone work
- Decimal phases (2.1, 2.2): Urgent insertions (marked with INSERTED)

Decimal phases appear between their surrounding integers in numeric order.

- [ ] **Phase 1: Walking Skeleton — CLI Wiring + Parquet/ZIP Foundation** - `-f mzpeak` produces a minimal valid archive; nested-schema Parquet.Net approach spike-validated
- [ ] **Phase 2: Spectra Signal Data** - Point-layout `spectra_data`/`spectra_peaks` with sorted m/z, honoring MS-level/scan-range filters
- [ ] **Phase 3: Spectra Metadata + File-Level Metadata/Index** - Packed parallel metadata tables and the full `mzpeak_index.json` metadata block
- [ ] **Phase 4: Chromatograms + Conformance Verification** - TIC chromatogram facets and round-trip verification against the reference reader

## Phase Details

### Phase 1: Walking Skeleton — CLI Wiring + Parquet/ZIP Foundation
**Goal**: Make `-f mzpeak` a real, dispatchable format that emits a minimal STORED ZIP containing a valid Parquet facet plus `mzpeak_index.json`, proving Parquet.Net's nested-schema capability end-to-end before any fidelity is added.
**Depends on**: Nothing (first phase)
**Requirements**: CLI-01, CLI-02, CLI-03, PQ-01, PQ-02, PQ-03
**Success Criteria** (what must be TRUE):
  1. `-f mzpeak` / `--format mzpeak` is accepted, listed in `--format` help text, and produces an output file with the `.mzpeak` extension.
  2. `RawFileParser` dispatch instantiates a new `Writer/MzPeakSpectrumWriter` (extending `SpectrumWriter`) for the format, and a conversion run completes without error.
  3. A spike proves Parquet.Net v5.0.1 can write a Parquet file containing a nested struct, a list-of-structs, and parallel nullable top-level columns via the low-level `ParquetSchema`/`DataColumn` API, with a round-trip read confirming the values.
  4. Reusable helpers exist to build the repeated `PARAM` value-struct and CV-accession-named columns (e.g. `MS_1000511_ms_level`).
  5. The produced archive is a STORED (uncompressed) ZIP with internally ZSTD-compressed Parquet, and the reference Python reader can OPEN the minimal archive without error.
**Key risks**:
  - **Primary make-or-break unknown:** Parquet.Net's low-level API may not cleanly express `large_list`/`large_string` (Arrow 64-bit-offset) variants or deeply nested list-of-struct columns. If the spike fails, the whole approach (and possibly the v1 layout) must be reconsidered — this is why it is front-loaded.
  - Reference reader may reject plain `string`/`list` where ground truth uses `large_string`/`large_list`; needs a round-trip read to confirm acceptance.
  - ZIP must be STORED at the archive level (no deflate); an accidental deflate would break reader compatibility.
**Plans**: 1 plan
  - [ ] 01-01-PLAN.md — Wire `-f mzpeak` end-to-end (enum/help/dispatch/stream), shared Parquet/CV helper with low-level round-trip proof, and minimal STORED-ZIP archive that passes the reference reader OPEN gate

### Phase 2: Spectra Signal Data
**Goal**: Emit lossless point-layout spectral signal — `spectra_data.parquet` (and `spectra_peaks.parquet` for centroids) — with canonical widths and ascending m/z, respecting the existing input filters.
**Depends on**: Phase 1
**Requirements**: DATA-01, DATA-02, DATA-03, DATA-04
**Success Criteria** (what must be TRUE):
  1. `spectra_data.parquet` is written in point layout `struct<spectrum_index:u64, mz:f64, intensity:f32>`, one row per data point.
  2. m/z is coerced to float64 and intensity to float32, with m/z sorted ascending per spectrum and the (m/z, intensity) multiset preserved (no points dropped or merged).
  3. When a centroid representation exists for a scan, its peaks are written to `spectra_peaks.parquet` (m/z f64, intensity f32) rather than only `spectra_data`.
  4. MS-level and scan-range filtering from the existing `ParseInput` filters is honored — filtered-out spectra produce no rows.
**Key risks**:
  - Sort-on-write must preserve the multiset under duplicate m/z values; a non-stable or value-merging sort would silently lose points.
  - Intensity narrowing f64→f32 is lossy if the source is f64; must apply uniformly and not break the round-trip tolerance used in Phase 4.
  - Centroid-vs-profile routing must follow the explicit representation, not be inferred from array shape.
**Plans**: TBD

### Phase 3: Spectra Metadata + File-Level Metadata/Index
**Goal**: Emit the packed-parallel `spectra_metadata.parquet` tables and the complete `mzpeak_index.json` metadata block, with all CV terms encoded as CURIEs via the established mapping.
**Depends on**: Phase 2
**Requirements**: META-01, META-02, META-03, META-04, META-05, IDX-01, IDX-02, IDX-03, IDX-04
**Success Criteria** (what must be TRUE):
  1. `spectra_metadata.parquet` contains four parallel top-level struct columns — `spectrum`, `scan`, `precursor`, `selected_ion` — each row populating exactly one, with the others null, and back-references via `source_index`.
  2. `spectrum` rows carry index/id/ms_level/time/polarity/representation/type/observed-m/z-range/counts/base-peak/TIC; `scan` rows carry scan_start_time (minutes)/filter_string/ion_injection_time/instrument_configuration_ref/scan_windows; `precursor` rows carry isolation window (target/lower/upper) + activation; `selected_ion` rows carry selected-ion m/z/charge/intensity.
  3. All CV terms are encoded as CURIEs and column names embed the accession (e.g. `MS_1000016_scan_start_time_unit_UO_0000031`) per the mzML-CV→mzPeak mapping table.
  4. `mzpeak_index.json` lists every present facet in `files[]` with `entity_type`/`data_kind`.
  5. The `metadata{}` block contains instrument_configuration_list (ionsource/analyzer/detector + CV params derived from Thermo instrument info via `OntologyMapping`), software_list (TRFP entry), data_processing_method_list (conversion + sort/narrowing provenance), file_description (source RAW + contents params), and cv_list (MS, UO).
**Key risks**:
  - Mapping Thermo instrument info to the instrument_configuration component model (ordered ionsource/analyzer/detector with CV params) via `OntologyMapping` may have gaps for some instrument families.
  - cv_list must be a superset of every accession actually referenced, or reference readers cannot resolve CURIEs — easy to drift if columns are added without registering their CVs.
  - Packed parallel-table null-discipline (exactly one populated struct per row) must hold across all four column families simultaneously in the low-level writer.
**Plans**: TBD

### Phase 4: Chromatograms + Conformance Verification
**Goal**: Add the TIC chromatogram facets and prove the whole archive round-trips losslessly against the reference reader, locked in by an automated NUnit test on `small.RAW`.
**Depends on**: Phase 3
**Requirements**: CHROM-01, CHROM-02, VER-01, VER-02, VER-03, VER-04
**Success Criteria** (what must be TRUE):
  1. `chromatograms_data.parquet` is written in point layout `struct<chromatogram_index:u64, time:f64, intensity:f32, ms_level:i64>` carrying the TIC.
  2. `chromatograms_metadata.parquet` contains the chromatogram struct (type=TIC CURIE, polarity, point count), and the chromatogram facets are listed in `mzpeak_index.json`.
  3. `mzpeak-validate <out>.mzpeak` exits 0 (no error-level findings) against profile mzpeak-0.9.
  4. Differential equivalence vs `mzML2mzPeak` for the same RAW (spectrum/peak counts, (m/z,intensity) multiset within f32 tol, ms_level, polarity, RT, precursor m/z/charge, TIC) and L1/L2 round-trip (m/z value-equal; intensity bounded-equal under the recorded f32 narrowing transform).
  5. An NUnit test converts `ThermoRawFileParserTest/Data/small.RAW` to mzPeak, asserts archive structure, and invokes `mzpeak-validate` (skip-with-warning if absent).
**Key risks**:
  - Reference reader may require a chromatogram metadata facet to exist at open time even when empty; the writer must register at least the TIC so readers do not error on a missing facet.
  - Round-trip tolerance for intensity must be defined to accommodate the lossy f64→f32 narrowing without masking real data loss.
  - The NUnit test depends on a reference Python/Rust reader being available in the test environment; harness availability may need a fallback structural assertion.
**Plans**: TBD

## Progress

**Execution Order:**
Phases execute in numeric order: 1 → 2 → 3 → 4

| Phase | Plans Complete | Status | Completed |
|-------|----------------|--------|-----------|
| 1. Walking Skeleton — CLI Wiring + Parquet/ZIP Foundation | 0/TBD | Not started | - |
| 2. Spectra Signal Data | 0/TBD | Not started | - |
| 3. Spectra Metadata + File-Level Metadata/Index | 0/TBD | Not started | - |
| 4. Chromatograms + Conformance Verification | 0/TBD | Not started | - |
