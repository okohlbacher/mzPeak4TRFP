# Phase 5 (CLI/Docs + Conformance & Corpus Re-Verification) + Chromatogram Chunking — Certification

## Scope delivered
- **CLI2-01/CLI2-02**: `--point` / `--lossless`(`--no-numpress`) / `--chunk-size` documented in option
  `--help` + `RUNNING.md`; chosen encodings self-described in `data_processing_method_list`.
- **VER2-01**: all 3 modes (default chunked+numpress, `--lossless`, `--point`) `mzpeak-validate` **0 errors**
  (only the validator's documented advisory `cv_term_placement_tables` warning).
- **VER2-02**: corpus differential re-run, **96/96 pairs**, 100% structural alignment (count, ms_level,
  polarity, RT); exact-multiset low by design (lossy SLOF reference vs our lossless f32 → ours more accurate).
- **VER2-03**: L1/L2 conformance locked by existing NUnit tests, native arm64.
- **VER2-04**: comparator vectorized (Arrow columns + NumPy `np.unique`), byte-identical output, ~11× faster;
  the lone 2.1 GB `COMPARE_ERROR`/timeout now completes in 775 s (< 900 s budget).
- **VER2-05**: 8.4 GB Astral + 2.1 GB Ascend convert + validate end-to-end.
- **Chromatogram chunking** (user-requested, the last reference-structural divergence): `chromatograms_data`
  now follows the spectra layout — one time-axis chunk per chromatogram. Default numpress-linear time
  (MS:1002312), `--lossless` delta (MS:1003089), **f64 intensity** (matches the reference); `--point` keeps
  the legacy per-point chromatogram with ms_level. New `ChromChunkFacetStream` + `IChromDataFacet`.

## Verification
- Build: native arm64 (AnyCPU 8.0.37), 0 errors.
- Tests: **97/97** green (added `Chromatogram_Chunk_Numpress_Shape` + `Chromatogram_Chunk_Lossless_Delta_
  RoundTrips_Time`; the latter delta-reconstructs the time axis EXACTLY against the `--point` chromatogram
  times). Point-layout output verified byte-identical (ChromDataFacetStream narrows f64→f32 as before).
- Conformance: `mzpeak-validate` **0 errors** in all 3 modes; the chunk chromatogram struct matches the
  reference field-for-field (`chromatogram_index, time_chunk_start/end, time_chunk_values, chunk_encoding,
  intensity, [time_numpress_linear_bytes]`). Validator infers the chunk layout from column names, so (like
  the reference) no chromatogram `array_index` is emitted in chunk mode.

## Adversarial review
- **External CLIs non-functional this session**: `vibe` broken throughout; `codex` produced verdicts for the
  early phases but the later runs (comparator + chromatogram certs) **hung on stdin** (`Reading additional
  input from stdin...`, ~1 h no output) and were killed. The cert was therefore performed as a structured
  self-review against the same 7-point checklist (def/rep levels vs the proven ChunkFacetStream; resource
  safety/Dispose; point byte-identity under the float→double CaptureTic change; footer/cv_list; single-chunk
  memory; numpress over the monotonic time axis + phantom-anchor trim; test adequacy). No defects found;
  every claim is backed by the 97/97 suite, the 3-mode 0-error validation, the exact delta round-trip, and
  the field-by-field reference comparison.
