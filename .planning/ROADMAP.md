# Roadmap: mzPeak Writer for ThermoRawFileParser — v2 ("compression, fidelity & scale")

## Overview

This roadmap takes the certified v1 point-layout mzPeak writer to its v2 goal: **smaller,
reference-structured output (chunked + Numpress-linear m/z) that scales to multi-GB RAW
files and is robust to imperfect scans — without regressing v1 conformance.** It is
sequenced operational-foundation-first. Phase 1 refactors the writer to bounded
row-group / streaming Parquet writes (constant memory) and STORED-zip streaming, and makes
per-scan read failures non-fatal — all while keeping the v1 point layout so it is
independently verifiable and unblocks the multi-GB corpus that OOMs today. Only on that
streaming base do the format changes land: chunked layout becomes the new default (Phase 2,
lossless delta), Numpress-linear m/z encoding makes it lossy-but-bounded and the size win
real (Phase 3), profile compaction + ion mobility add the remaining fidelity (Phase 4), and
Phase 5 documents the CLI, hardens the comparator, and re-verifies every mode against the
validator and the full 97-pair corpus, including multi-GB end-to-end. Each phase is
independently verifiable and unblocks the next.

**Process note (not a deliverable):** This project mandates an external adversarial code
review using the `codex` and `vibe` CLIs at the START (plan) and CLOSE (certification) of
every phase. This is a quality gate around each phase, not work inside any phase, and must
never appear in the produced code or output schema.

## Phases

**Phase Numbering:**

- Integer phases (1, 2, 3): Planned milestone work
- Decimal phases (2.1, 2.2): Urgent insertions (marked with INSERTED)

Decimal phases appear between their surrounding integers in numeric order.

- [ ] **Phase 1: Streaming Writer + Per-Scan Robustness** - Refactor to bounded row-group / streaming Parquet + STORED-zip streaming (constant memory), make per-scan read failures non-fatal; point layout unchanged so it is independently verifiable
- [ ] **Phase 2: Chunked Layout** - Emit `spectra_data`/`spectra_peaks` as the reference chunk struct with fixed m/z-window chunking (lossless delta), make chunked the default, add `--point` opt-out
- [ ] **Phase 3: Numpress-Linear m/z** - C# MSNumpress port encoding m/z into `mz_numpress_linear_bytes` with the MS:1003089 transform recorded; default ON, `--no-numpress`/`--lossless` opt-out, L2 bound
- [ ] **Phase 4: Profile Compaction + Ion Mobility** - Zero-run stripping + null-marking with the per-spectrum δmz model for profile data; populate ion-mobility values/type from Thermo FAIMS CV
- [ ] **Phase 5: CLI/Docs + Conformance & Corpus Re-Verification** - Document all flags, harden the comparator for large facets, re-run the 97-pair corpus + multi-GB validation, lock L1/L2 conformance; all modes pass `mzpeak-validate`

## Phase Details

### Phase 1: Streaming Writer + Per-Scan Robustness

**Goal**: Refactor the mzPeak writer to stream spectra into bounded Parquet row groups and stream STORED-zip assembly (constant memory, independent of run size) and make a single bad scan non-fatal — all without changing the v1 point layout or regressing its conformance.
**Depends on**: Nothing (first phase of v2)
**Requirements**: MEM-01, MEM-02, MEM-03, ROB-01, ROB-02
**Success Criteria** (what must be TRUE):

  1. The writer flushes Parquet in bounded row groups under a configurable cap on rows/bytes, so peak memory stays constant rather than accumulating full facets — verified by a converting run whose memory is independent of spectrum count (MEM-01).
  2. STORED-zip assembly streams facet bytes into the archive without holding the whole archive in memory, so peak memory is independent of total output size (MEM-03).
  3. A multi-GB corpus RAW (e.g. the ~1 GB Orbitrap and 9 GB Astral files) converts to mzPeak end-to-end without OOM, and the output passes `mzpeak-validate` (MEM-02).
  4. A single scan read failure (e.g. "Cannot get scan event for N") is logged + counted and SKIPPED, conversion continues, and the whole archive is never aborted — mirroring `MzMlSpectrumWriter` behaviour (ROB-01).
  5. A run containing skipped scans still emits a valid archive of the good scans, with facet and metadata spectrum sets consistent: skipped scans are absent from all facets and ordinals/indices remain dense (ROB-02).

**Key risks**:

  - **Primary risk:** refactoring the writer's buffering/index assignment to streaming without regressing v1 conformance — index/`sorting_rank`/multiset invariants that v1 locked must survive the move from full-facet accumulation to incremental row groups.
  - Dense-ordinal bookkeeping after a mid-run skip: the spectrum index space must stay gap-free across spectra_data, spectra_peaks, and the four metadata tables simultaneously when a scan is dropped after partial state is emitted.
  - Multi-GB validation depends on the large corpus files being available in the run environment; absence would force a smaller proxy and a deferred MEM-02 confirmation.

**Plans**: TBD

### Phase 2: Chunked Layout

**Goal**: Emit `spectra_data` and `spectra_peaks` as the reference chunk struct with fixed m/z-window chunking (lossless delta encoding), making chunked the new default while `--point` restores the v1 layout.
**Depends on**: Phase 1
**Requirements**: CHUNK-01, CHUNK-02, CHUNK-03, CHUNK-04, CHUNK-05, CHUNK-06
**Success Criteria** (what must be TRUE):

  1. `spectra_data` (and `spectra_peaks`) are emitted as the reference chunk struct `chunk<spectrum_index:u64, mz_chunk_start:f64, mz_chunk_end:f64, mz_chunk_values:list<f64>, chunk_encoding:string, intensity:list<f32>, mz_numpress_linear_bytes:list<u8>>`, with the `mz_numpress_linear_bytes` column present (empty/null in lossless mode) (CHUNK-01).
  2. Chunking is a fixed m/z window over the sorted m/z axis (default 50 m/z, configurable), one chunk row per non-empty window per spectrum, with `mz_chunk_start`/`mz_chunk_end` bounding the window (CHUNK-02).
  3. `mz_chunk_values` is delta-encoded with `chunk_encoding="delta"` (lossless) when Numpress is off (CHUNK-03).
  4. `spectrum_array_index` describes the chunk buffer formats (chunk_start/end/values/encoding) with `sorting_rank:0`, and the `cv_list` stays exhaustive over every referenced accession (CHUNK-04).
  5. Chunked is the new default output and the `--point` flag restores the v1 point layout (CHUNK-05); chunked output passes `mzpeak-validate` and round-trips the (m/z, intensity) multiset exactly in lossless mode (CHUNK-06).

**Key risks**:

  - Chunk-boundary correctness under empty windows and per-spectrum window counts: an off-by-one or dropped boundary point would silently lose data or break the validator's chunk-format checks.
  - Round-trip multiset preservation through window splitting + delta encoding must be exact in lossless mode — delta accumulation must not introduce f64 drift across long runs.
  - `spectrum_array_index` and `cv_list` must be updated in lockstep with the new chunk columns; a stale array-index description or missing CURIE would fail readers/validate even with correct bytes.

**Plans**: TBD

### Phase 3: Numpress-Linear m/z

**Goal**: Encode m/z with a vendored/ported C# MSNumpress-linear codec into `mz_numpress_linear_bytes`, record the MS:1003089 transform in the array index and a data_processing step, and make Numpress the default with a documented lossless opt-out.
**Depends on**: Phase 2
**Requirements**: NP-01, NP-02, NP-03, NP-04
**Success Criteria** (what must be TRUE):

  1. A C# Numpress-linear encode (and a decode used only by tests) exists as vendored/ported MSNumpress with no x64-only dependencies, building and running native arm64 (NP-01).
  2. m/z is encoded into `mz_numpress_linear_bytes` with `chunk_encoding` set accordingly, and the transform CURIE **MS:1003089** is recorded in BOTH `spectrum_array_index` AND a `data_processing` step (NP-02).
  3. Numpress is ON by default; `--no-numpress` / `--lossless` instead produces delta chunks, the lossy-m/z choice is noted in `data_processing`, and a CLI warning is emitted when lossy m/z is in effect (NP-03).
  4. L2 conformance holds: decoded m/z is within the Numpress-linear bound versus source, while intensity stays lossless f32 — verified by an encode/decode round-trip test against the recorded bound (NP-04).

**Key risks**:

  - **Primary unknown:** a correct C# Numpress-linear port that matches the reference's exact byte stream and tolerance — fixed-point scaling, integer overflow, and edge cases (single point, very close m/z) must match MSNumpress semantics, not merely "a numpress that decodes."
  - The recorded L2 bound must be the actual codec bound; a too-loose tolerance would mask encoding bugs while a too-tight one would falsely fail conformance.
  - The transform must be recorded consistently in two places (array index + data_processing); divergence between them would make the output self-contradict its provenance.

**Plans**: TBD

### Phase 4: Profile Compaction + Ion Mobility

**Goal**: Compact profile spectra via zero-run stripping + null-marking with a per-spectrum δmz reconstruction model in `spectrum.mz_delta_model` (centroids untouched), and populate ion-mobility values/type from the Thermo FAIMS CV.
**Depends on**: Phase 3
**Requirements**: ZRS-01, ZRS-02, ZRS-03, ZRS-04, IM-01, IM-02
**Success Criteria** (what must be TRUE):

  1. Zero-run stripping removes interior runs of zero-intensity profile points (flanking zeros kept), leaving peak apex/centroid unaffected (ZRS-01).
  2. Null-marking replaces flanking zeros with null m/z+intensity, and a per-spectrum δmz model (β0+β1·mz+β2·mz², weighted least squares) is stored in `spectrum.mz_delta_model` for reconstruction (ZRS-02).
  3. Stripping/marking is flag-controlled, applied only to profile spectra, and reconstruction is verified near-lossless — peak shape preserved within tolerance (ZRS-03); centroid facets (`spectra_peaks`) are untouched by stripping/marking (ZRS-04).
  4. `scan.ion_mobility_value` + `ion_mobility_type` are populated from the Thermo FAIMS scan-trailer (`FAIMS CV` / `FAIMS Voltage On`) with CV term MS:1001581 (FAIMS compensation voltage) (IM-01).
  5. `selected_ion` ion-mobility is populated where applicable, and spectra without FAIMS leave the ion-mobility columns null as in v1 (IM-02).

**Key risks**:

  - **Primary risk:** δmz-model reconstruction correctness — the WLS quadratic fit must reconstruct null-marked m/z within tolerance across the full m/z range; a poor fit at the spectrum edges would distort reconstructed peak shape and break near-losslessness.
  - Distinguishing interior vs flanking zeros and profile vs centroid spectra must be exact, so apex/centroid points are never stripped and centroid facets are provably untouched.
  - FAIMS trailer parsing varies by acquisition; absent/ambiguous trailer keys must leave columns null (not zero) to preserve the v1 contract for non-FAIMS runs.

**Plans**: TBD

### Phase 5: CLI/Docs + Conformance & Corpus Re-Verification

**Goal**: Document every new flag, record chosen encodings in provenance, harden the e2e comparator for large facets, and re-verify all modes against `mzpeak-validate` and the full 97-pair corpus (including multi-GB) with L1/L2 conformance locked in NUnit.
**Depends on**: Phase 4
**Requirements**: CLI2-01, CLI2-02, VER2-01, VER2-02, VER2-03, VER2-04, VER2-05
**Success Criteria** (what must be TRUE):

  1. The new flags — `--point`, `--no-numpress`/`--lossless`, chunk-size, null-marking toggle — are documented in the `--format` help text and `RUNNING.md`, with sensible defaults (chunked + numpress) (CLI2-01); chosen encodings are recorded in `data_processing_method_list` so the output self-describes its transforms (CLI2-02).
  2. All modes — default chunked+numpress, `--lossless`, and `--point` — pass `mzpeak-validate` with 0 errors (VER2-01).
  3. The e2e comparator is hardened for large facets (the v1-sweep `COMPARE_ERROR`s) so the full 97-pair corpus completes (VER2-04), and the differential re-run reports per-file deltas showing the exact-match rate rises materially versus v1 now that chunk+numpress structurally match the reference (VER2-02).
  4. Multi-GB corpus files (Astral 9 GB, ~1 GB Orbitrap) convert + validate end-to-end (VER2-05).
  5. L1 (lossless modes) and L2 (numpress) conformance are locked by NUnit tests running native arm64 (VER2-03).

**Key risks**:

  - Comparator hardening for large facets may surface fundamental memory/throughput limits in the e2e harness itself, not just our output, requiring streaming comparison to complete the 97-pair sweep.
  - L1/L2 NUnit locks depend on the validator and reference converter being available in the test environment; harness availability may force skip-with-warning fallbacks for some assertions.
  - The "exact-match rate rises materially vs v1" claim (VER2-02) is contingent on Phase 3 byte-matching the reference; residual numpress/δmz tolerance differences could keep some pairs in bounded-but-not-exact territory and must be reported honestly rather than masked.

**Plans**: TBD

## Progress

**Execution Order:**
Phases execute in numeric order: 1 → 2 → 3 → 4 → 5

| Phase | Plans Complete | Status | Completed |
|-------|----------------|--------|-----------|
| 1. Streaming Writer + Per-Scan Robustness | 0/0 | Not started | - |
| 2. Chunked Layout | 0/0 | Not started | - |
| 3. Numpress-Linear m/z | 0/0 | Not started | - |
| 4. Profile Compaction + Ion Mobility | 0/0 | Not started | - |
| 5. CLI/Docs + Conformance & Corpus Re-Verification | 0/0 | Not started | - |
