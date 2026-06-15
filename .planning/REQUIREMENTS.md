# Requirements: mzPeak Writer for ThermoRawFileParser — v2 ("compression, fidelity & scale")

**Defined:** 2026-06-14
**Builds on:** v1 (point-layout writer, certified; archived under `.planning/archive/v1-point-layout/`).
**Core Value:** Smaller, reference-structured mzPeak output (chunked + Numpress) that scales to multi-GB
RAW files and is robust to imperfect scans — without regressing v1 conformance.

## Milestone decisions (locked)

- **New defaults:** output is **chunked layout + Numpress-linear m/z** (lossy m/z, transform recorded).
  `--point` restores v1 point layout; `--lossless` / `--no-numpress` keeps chunked but lossless (delta).

- **Scope:** format features **and** operational hardening (streaming + per-scan robustness).
- **Non-negotiable:** every mode still passes `mzpeak-validate`; lossless modes preserve the v1
  (m/z, intensity) multiset exactly; lossy (Numpress) modes are bounded under the recorded transform (L2).

## v2 Requirements

### Streaming writer (operational foundation)

- [ ] **MEM-01**: Writer streams spectra and flushes Parquet in bounded row groups (configurable cap on rows/bytes) — constant memory, not full-facet accumulation
- [ ] **MEM-02**: Multi-GB RAW (e.g. the ~1 GB Orbitrap and 9 GB Astral corpus files) convert without OOM, output validates
- [ ] **MEM-03**: STORED-zip assembly streams facet bytes (no whole-archive-in-memory) so peak memory is independent of run size

### Per-scan robustness

- [ ] **ROB-01**: A single scan read failure (e.g. "Cannot get scan event for N") is logged + counted and SKIPPED; conversion continues (mirrors `MzMlSpectrumWriter`), never aborts the whole archive
- [ ] **ROB-02**: A run with skipped scans still emits a valid archive of the good scans, with facet/metadata spectrum sets consistent (skipped scans absent from all facets, ordinals dense)

### Chunked layout

- [x] **CHUNK-01**: `spectra_data` emitted as the reference 6-field chunk struct `chunk<spectrum_index:u64, mz_chunk_start:f64, mz_chunk_end:f64, mz_chunk_values:large_list<f64>, chunk_encoding:string, intensity:large_list<f32>>` (the 7th `mz_numpress_linear_bytes` field is added only in Numpress mode, Phase 3). `spectra_peaks`/`chromatograms_data` stay point (chromatogram chunking deferred to Phase 5)
- [x] **CHUNK-02**: Chunking = fixed m/z window over the sorted m/z axis (default 50 m/z, configurable); one chunk row per non-empty window per spectrum; `mz_chunk_start`/`end` bound the window
- [x] **CHUNK-03**: `mz_chunk_values` delta-encoded with `chunk_encoding=MS:1003089` (lossless) when Numpress is off
- [x] **CHUNK-04**: `spectrum_array_index` describes chunk buffer formats (chunk_start/end/values/encoding) + `sorting_rank:0`; cv_list stays exhaustive
- [x] **CHUNK-05**: `--point` flag restores the v1 point layout; chunked is the new default
- [x] **CHUNK-06**: Chunked output passes `mzpeak-validate` and round-trips the (m/z, intensity) multiset exactly in lossless mode

### Numpress-linear m/z

- [x] **NP-01**: C# Numpress-linear encode (and decode for tests) — vendored/ported MSNumpress, no x64-only deps
- [x] **NP-02**: m/z encoded into `mz_numpress_linear_bytes` with `chunk_encoding` set accordingly; transform CURIE **MS:1003089** recorded in `spectrum_array_index` AND a `data_processing` step
- [x] **NP-03**: Numpress ON by default; `--no-numpress` / `--lossless` produces delta chunks instead; lossy-m/z noted in `data_processing` + a CLI warning
- [x] **NP-04**: L2 conformance — decoded m/z within the Numpress-linear bound vs source; intensity stays lossless f32

### Null-marking / zero-run stripping (profile data)

- [~] **ZRS-01** (DEFERRED → BL-02): Zero-run stripping — interior runs of zero-intensity profile points removed (flanking zeros kept); peak apex/centroid unaffected
- [~] **ZRS-02** (DEFERRED → BL-02): Null-marking — flanking zeros replaced with null m/z+intensity; per-spectrum δmz model (β0+β1·mz+β2·mz², weighted least squares) stored in `spectrum.mz_delta_model` for reconstruction
- [~] **ZRS-03** (DEFERRED → BL-02): Flag-controlled; reconstruction verified near-lossless (peak shape preserved within tolerance); only applied to profile spectra
- [~] **ZRS-04** (DEFERRED → BL-02): Centroid facets (`spectra_peaks`) untouched by stripping/marking

### Ion-mobility values

- [x] **IM-01**: Populate `scan.ion_mobility_value` + `ion_mobility_type` from the Thermo FAIMS scan-trailer (`FAIMS CV` / `FAIMS Voltage On`), CV term MS:1001581 (FAIMS compensation voltage)
- [x] **IM-02**: `selected_ion` ion-mobility populated where applicable; spectra without FAIMS leave the columns null (no Thermo source for selected-ion mobility → null, as today)

### CLI & docs

- [x] **CLI2-01**: New flags — `--point`, `--no-numpress`/`--lossless`, `--chunk-size` — documented in option `--help` + `RUNNING.md`; sensible defaults (chunked+numpress). (Null-marking toggle dropped with ZRS → BL-02.)
- [x] **CLI2-02**: Provenance: chosen encodings recorded in `data_processing_method_list` (file format conversion + intensity narrowing + Numpress-linear m/z step when lossy) so the output self-describes its transforms

### Conformance & corpus re-verification

- [ ] **VER2-01**: All modes (default chunked+numpress, `--lossless`, `--point`) pass `mzpeak-validate` (0 errors)
- [ ] **VER2-02**: E2E corpus differential re-run — with chunk+numpress matching the reference structure, exact-match rate rises materially vs v1; report per-file deltas
- [ ] **VER2-03**: L1 (lossless modes) / L2 (numpress) conformance locked by NUnit tests, native arm64
- [ ] **VER2-04**: Comparator hardened for large facets (the v1-sweep `COMPARE_ERROR`s) so the full 97-pair corpus completes
- [ ] **VER2-05**: Multi-GB corpus files (Astral 9 GB, ~1 GB Orbitrap) convert + validate end-to-end

## Out of Scope (v2)

| Feature | Reason |
|---------|--------|
| imzML / imaging spatial extension | Not applicable to Thermo LC-MS |
| mzPeak → RAW/mzML reverse conversion | TRFP one-directional |
| Numpress for intensity (SLOF/pic) | m/z Numpress is the size win; intensity stays lossless f32 unless a later need arises |
| Richer mzLib MGF validation | Backlog BL-01 (mzLib x64-only) |

## Traceability (to be finalized by the roadmap)

| Requirement group | Phase |
|---|---|
| MEM-*, ROB-* | Phase 1 (streaming + robustness foundation) |
| CHUNK-* | Phase 2 (chunked layout) |
| NP-* | Phase 3 (Numpress) |
| ZRS-*, IM-* | Phase 4 (profile compaction + ion mobility) |
| CLI2-*, VER2-* | Phase 5 (CLI/docs + conformance & corpus re-verify) |

## Testing tools (unchanged from v1)

- **`~/Claude/mzPeakValidator`** — `mzpeak-validate` conformance oracle (the gate, VER2-01).
- **`~/Claude/mzML2mzPeak`** — reference converter + corpus; the E2E harness in `tools/e2e/` (VER2-02/04).

---
*Requirements defined: 2026-06-14 (v2)*
