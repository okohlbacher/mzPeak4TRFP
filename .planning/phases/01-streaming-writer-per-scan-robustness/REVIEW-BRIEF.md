# v2 Phase 1 plan review brief (plan gate)

Adversarial review of the PLAN. Read-only; findings only.

## Context

v2 Phase 1 = make the writer stream (constant memory, multi-GB RAW) and survive bad scans, WITHOUT
changing output format (point layout unchanged → verifiable against v1 conformance). RESEARCH.md
(empirically verified) established: multi-row-group streaming reads as one table; `ParquetWriter` on a
non-seekable stream SILENTLY buffers all in RAM (→ temp-file mandatory); the ROB bug is early
point-append before the metadata/ordinal commit (fix = per-scan all-or-nothing).

## Read (repo root /Users/kohlbach/Claude/mzPeak4TRFR)

- .planning/phases/01-streaming-writer-per-scan-robustness/01-01-PLAN.md   ← THE PLAN
- .planning/phases/01-streaming-writer-per-scan-robustness/RESEARCH.md (authoritative)
- .planning/phases/01-streaming-writer-per-scan-robustness/CONTEXT.md
- ThermoRawFileParser/Writer/MzPeakSpectrumWriter.cs, Writer/MzPeak/MzPeakParquet.cs (current code)
- ThermoRawFileParser/Writer/MzMlSpectrumWriter.cs (robustness pattern)

## Evaluate

1. CORRECTNESS of the streaming design: one ParquetWriter + N row groups to a seekable temp file, then
   bounded CopyTo into a STORED zip entry. Is the seekable-sink guard actually enforced everywhere a facet
   is written (so the silent-buffer trap can't sneak back)? Temp-file lifecycle (cleanup on success AND on
   exception)? leaveOpen / disposal ordering of ParquetWriter vs temp FileStream vs ZipArchive?
2. NO v1 REGRESSION: will multi-row-group output be byte-semantically identical (same logical table, same
   (m/z,intensity) multiset, same metadata + counts, validator 0/0) to v1? Any way row-group splitting
   changes spectrum_index contiguity, footer KV (spectrum_count / array_index), or facet parity?
3. ROB all-or-nothing: does staging-then-commit truly leave ordinals dense and facet/metadata parity intact
   when a scan throws at ANY point (before/after partial data/peaks/precursor append)? Are the staged
   buffers (data, peaks, metadata, precursor/selected_ion, scanNumber→ordinal map, TIC/chromatogram) ALL
   committed atomically, or can one (e.g. the precursor/scan-number map or the chromatogram TIC point) leak
   on a partial failure? Call out any state mutated before the commit point.
4. METADATA-stays-buffered decision: is it sound that metadata + chromatograms_metadata remain in memory
   (bounded by spectrum count)? For a 9 GB / ~100k-spectrum run, is that actually bounded enough, or should
   it be flagged? Is the single-pass count/base-peak/TIC computation correct under streaming?
5. CHROMATOGRAM TIC: it's 1:1 with scans (Phase v1) — if a scan is skipped (ROB), does the TIC point for
   that scan get skipped too (parity between chromatograms_data and the emitted spectrum set)? Is that handled?
6. ACCEPTANCE DECISIVENESS + runtime env (native arm64, DOTNET_ROLL_FORWARD, bin/Release) in commands; the
   MEM-02 RSS measurement is decisive; reuse of MzPeakParquet (no fork); no harness/process/phase comments.

## Output

Prioritized findings: SEVERITY (BLOCKER/HIGH/MEDIUM/LOW/NIT), location, problem, concrete fix. End with one
line: VERDICT: SHIP / SHIP-WITH-FIXES / REWORK.
