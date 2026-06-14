# Phase 1 Context (v2): Streaming Writer + Per-Scan Robustness

**Requirements:** MEM-01, MEM-02, MEM-03, ROB-01, ROB-02
**Milestone:** v2. **Depends on:** v1 writer (archived plan under `.planning/archive/v1-point-layout/`).

## Intent

Make the writer convert arbitrarily large RAW files in constant memory and survive individual bad
scans — WITHOUT changing the output format yet (point layout stays, so this phase is verifiable
against the v1 conformance bar). This is the operational foundation the chunked/Numpress phases build on.

## Problem (v1 today)

`MzPeakSpectrumWriter` accumulates EVERY spectrum's points in memory, builds each Parquet facet fully
in a `MemoryStream` via `MzPeakParquet.WriteAsync` (a single row group), then copies bytes into the
STORED zip. On multi-GB RAW (e.g. the 9 GB Astral, ~1 GB Orbitrap corpus files) this OOMs. A single
unreadable scan (observed: `Cannot get scan event for N`) currently aborts the whole conversion.

## Decisions (locked / strong defaults — research may refine)

- **Stream the unbounded (point/data) facets** — `spectra_data`, `spectra_peaks`, `chromatograms_data` —
  in bounded row groups: open one `ParquetWriter` per facet, accumulate a row-group buffer, flush when it
  reaches a cap (configurable rows or ~bytes), repeat, close at end. Peak memory = one row-group buffer
  per open facet, independent of total point count.
- **Seekable target via temp files.** Parquet needs a seekable stream (footer on close), but a STORED
  zip-entry stream is not seekable. Write each facet to a temp file (seekable, streamed row groups), then
  at finalize **stream-copy** each temp file into its STORED zip entry and delete it. Constant memory,
  bounded temp disk. (MEM-03)
- **Metadata facets may stay buffered for this phase.** `spectra_metadata` / `chromatograms_metadata` are
  bounded by spectrum count (not point count) — small relative to the data facets — so they can remain
  in-memory in v1's form. If profiling shows they matter for very large runs, stream them too; otherwise
  defer. State the chosen approach in the SUMMARY. (Keeps the co-resident packed-table writer untouched,
  reducing regression risk.)
- **`MzPeakParquet` gains a multi-row-group streaming path** (open → write row group → … → close) in
  addition to the existing one-shot `WriteAsync`. Existing point/chunk schema + column logic reused.
- **Per-scan robustness (ROB):** wrap each scan's read/append in try/catch; on failure, log + count via
  `ParseInput.NewError()`/`NewWarning()` and SKIP the scan (no rows in any facet, ordinal NOT consumed),
  mirroring `MzMlSpectrumWriter`'s per-scan handling. Conversion continues and still emits a valid archive.
  Skipped scans must leave data/metadata spectrum sets consistent (dense ordinals, facet parity preserved).

## Must NOT regress (v1 conformance)

- Output still passes `mzpeak-validate` (0/0) and is byte-semantically identical to v1 point-layout output
  for a clean file (same spectra, same (m/z,intensity) multiset, same metadata/counts). Row-group chunking
  is an internal Parquet detail — readers see the same logical table.
- Facet parity: `spectrum_count` == distinct `spectrum_index` in data == metadata spectrum rows.

## Verification (this phase)

- Build + 52/52 tests native arm64.
- Convert `small.RAW` → identical logical output to v1 (validator 0/0; counts unchanged); add a test asserting
  multi-row-group output reads back identically.
- **MEM-02:** convert a LARGE corpus file (e.g. a ~600 MB–1 GB Orbitrap from `~/Claude/mzML2mzPeak/data`,
  and ideally attempt the 9 GB Astral) under a bounded memory budget — succeeds + validates. Measure peak RSS.
- **ROB:** convert a file with a known bad scan (the corpus `S4_5foldGHRP.raw`, which fails on scan #25081)
  → completes, logs the skip, emits a valid archive; assert the skipped scan is absent and parity holds.

## Constraints / runtime

- Native arm64 (AnyCPU, RawFileReader 8.0.37); `DOTNET_ROLL_FORWARD` for net8→net9/10; `dotnet test` native.
- Reuse `MzPeakParquet` (extend, don't fork). Compact code; explicit usings; BOM-free new files; NO comments
  referencing harness/process/phases.
- Validator: `mzpeak-validate`. Corpus + big files under `~/Claude/mzML2mzPeak/data`.

## Reference artifacts

- `.planning/archive/v1-point-layout/phases/` (v1 writer plans/summaries — current writer/helper design).
- `ThermoRawFileParser/Writer/MzPeakSpectrumWriter.cs`, `Writer/MzPeak/MzPeakParquet.cs` (current code).
- `ThermoRawFileParser/Writer/MzMlSpectrumWriter.cs` (per-scan try/catch + NewError robustness pattern).
- `tools/e2e/` (corpus harness; `S4_5foldGHRP.raw` bad-scan case).
