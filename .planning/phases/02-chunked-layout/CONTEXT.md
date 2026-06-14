# v2 Phase 2 Context: Chunked Layout

**Requirements:** CHUNK-01..06
**Milestone:** v2. **Depends on:** Phase 1 (streaming writer + handle).

## Intent

Emit the data facets (`spectra_data`, `spectra_peaks`) in the reference **chunk layout** instead of point
layout, and make chunked the **new default** (`--point` restores v1 point layout). This phase is
lossless (delta-encoded m/z); Numpress (Phase 3) plugs into the `mz_numpress_linear_bytes` slot later.
Chromatograms stay point layout (the reference keeps them point).

## Authoritative references

- **`refs/mzPeak/small.chunked.mzpeak`** — the HUPO reference writer's OWN chunked output (same source as
  our test file). THE schema/encoding to match (column names, types, chunk_encoding values, delta layout,
  whether `mz_numpress_linear_bytes` is present-but-empty in delta mode).
- `refs/_findings/mzpeak_groundtruth_schema.md`, `refs/mzPeak/schema/array_index.json` (chunk buffer formats).
- v1 differential decode (archived) used: first point at `mz_chunk_start`, then `mz += delta` over
  `mz_chunk_values` (N-1 deltas, N intensities) — confirm exactly against small.chunked.mzpeak.

## Decisions (strong defaults — research confirms against small.chunked.mzpeak)

- **Chunk struct** per the reference: `chunk<spectrum_index:u64, mz_chunk_start:f64, mz_chunk_end:f64,
  mz_chunk_values:list<f64>, chunk_encoding:string, intensity:list<?>, mz_numpress_linear_bytes:list<u8>>`.
  **Confirm intensity element type** (the HUPO reference vs the mzML2mzPeak corpus differ — match
  `small.chunked.mzpeak`; if it's f64, decide whether to keep our canonical f32 and accept the difference,
  or match the reference — research to recommend, validator to arbitrate).
- **Chunking:** fixed m/z window over the sorted m/z axis, default 50 m/z (configurable). One chunk row per
  non-empty window per spectrum. `mz_chunk_start`/`end` = first/last actual m/z in the window (confirm vs
  reference: window-boundary vs actual-extent).
- **Encoding:** `chunk_encoding="delta"` (lossless). `mz_chunk_values` = consecutive deltas; first m/z is
  `mz_chunk_start`. `mz_numpress_linear_bytes` empty/null in delta mode (Phase 3 fills it).
- **Streaming:** reuse the Phase-1 streaming handle; chunk rows stream in row groups exactly like point rows
  (now the row "unit" is a chunk, not a point — far fewer rows). Needs list<f64>/list<f32> scalar-list
  columns with rep/def levels (the Phase-3-of-v1 list machinery exists; list-of-scalar is simpler).
- **`spectrum_array_index`** updated to chunk buffer formats (chunk_start / chunk_end / chunk_values with
  encoding/transform) + `sorting_rank:0`; cv_list stays exhaustive (any new transform/encoding CURIE registered).
- **`--point` flag:** restores v1 point layout; chunked is default. Both must pass mzpeak-validate.
- **chromatograms_data:** stays point layout (reference does).

## Must NOT regress

- mzpeak-validate 0/0 in BOTH chunked (default) and `--point` modes.
- Lossless: decoding chunked output reproduces the EXACT (m/z, intensity) multiset that v1 point layout
  produced for the same RAW (per spectrum). Facet parity preserved; streaming/memory behavior preserved.

## Verification (this phase)

- Build + full suite green native arm64; add tests: chunk round-trip == point multiset (per spectrum);
  chunk_encoding/array_index correct; `--point` still works; reader sees chunked output (mzpeak-validate 0/0).
- Diff our chunked spectra_data schema vs `refs/mzPeak/small.chunked.mzpeak` (pyarrow): column names/types/
  chunk_encoding match.
- small.RAW chunked vs v1 point: identical decoded multiset; chunked file is smaller.

## Constraints / runtime

- Native arm64 (AnyCPU 8.0.37); DOTNET_ROLL_FORWARD; bin/Release; native `dotnet`/`arch -arm64`; `mzpeak-validate`.
- Reuse MzPeakParquet + the Phase-1 streaming handle (extend, don't fork). Compact code; explicit usings;
  BOM-free new files; NO comments referencing harness/process/phases.

## Reference artifacts

- `refs/mzPeak/small.chunked.mzpeak` (authoritative chunk output); `refs/mzPeak/schema/array_index.json`.
- `.planning/phases/01-streaming-writer-per-scan-robustness/01-01-SUMMARY.md` (streaming handle API).
- `ThermoRawFileParser/Writer/MzPeakSpectrumWriter.cs`, `Writer/MzPeak/MzPeakParquet.cs`.
