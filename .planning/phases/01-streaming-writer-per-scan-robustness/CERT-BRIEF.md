# v2 Phase 1 certification review brief (close gate)

Adversarial review of the IMPLEMENTED code. Read-only; findings only.

## What shipped

Streaming writer: data facets (spectra_data, spectra_peaks, chromatograms_data) now stream as
multi-row-group Parquet to seekable temp files, then bounded-CopyTo into STORED zip entries; final
footer KV set via `CloseAsync(finalMetadata)` after row groups. Metadata facets stay buffered. Per-scan
all-or-nothing commit (incl. the `_precursorScanNumbers` map). 60/60 tests native arm64. MEM-02: 954 MB
Orbitrap → 2.04 GB RSS (validate 0/0); 8.4 GB Astral → no OOM (--quick 0/0).

## Read (repo root /Users/kohlbach/Claude/mzPeak4TRFR)

- ThermoRawFileParser/Writer/MzPeak/MzPeakParquet.cs   ← streaming handle (OpenAsync/WriteRowGroupAsync/CloseAsync) + AddStoredFromFile
- ThermoRawFileParser/Writer/MzPeakSpectrumWriter.cs   ← streaming data facets + per-scan commit
- ThermoRawFileParser/ThermoRawFileParserTest/  (new streaming/robustness tests)
- .planning/phases/01-streaming-writer-per-scan-robustness/01-01-PLAN.md, RESEARCH.md, REVIEW-SYNTHESIS.md, 01-01-SUMMARY.md

## Evaluate

1. STREAMING CORRECTNESS: seekable temp-file requirement enforced everywhere (no facet can hit a
   non-seekable sink → silent RAM buffering)? Disposal/finalize order (flush residual row group → set
   final metadata → dispose writer → dispose temp FileStream → AddStoredFromFile → delete in finally)
   correct and exception-safe? Temp files ALWAYS deleted (success AND failure paths)? Any path that still
   accumulates a whole facet in a MemoryStream/byte[]?
2. ATOMIC COMMIT: is the per-scan commit truly all-or-nothing? Is EVERY staged item (data, peaks, Record,
   filterKey, `_precursorScanNumbers`, scanNumber→ordinal, TIC) applied only on success and nothing on
   failure? Is a WRITE/FLUSH failure in the commit block propagated (fatal) rather than swallowed as a skip?
   Can a skipped scan still affect later precursor resolution?
3. NO v1 REGRESSION: logical output byte-semantically identical to v1 for a clean file (same multiset,
   metadata, counts, footer KV); validator 0/0; facet parity. Does row-group splitting change anything a
   reader sees? cv_list still complete (chrom-data prefixes registered before BuildMetadataFacet)?
4. RESOURCE/CONCURRENCY: stream/handle disposal, async-over-sync (.GetAwaiter().GetResult()) hazards,
   temp-dir collisions, the InternalsVisibleTo test seam (production default cap unchanged at 1,048,576).
5. MEM nuance: metadata stays buffered (bounded by spectrum count) — is that acknowledged and is there any
   unbounded-by-points growth left in the data path? (Astral RSS tracked spectrum count, expected.)
6. BLOAT/STYLE: dead code, duplication vs reuse of MzPeakParquet, redundant comments, any harness/process/
   phase comment (forbidden) — exact lines; BOM on new files.
7. TEST QUALITY: do the new tests actually lock multi-row-group identical-readback, the seekable-sink
   rejection, the final-metadata-after-rowgroups round-trip, and the atomic-commit/skipped-scan-isolation —
   or are any vacuous?

## Output

Prioritized findings: SEVERITY (BLOCKER/HIGH/MEDIUM/LOW/NIT), file:line, problem, concrete fix. End with one
line: VERDICT: CERTIFY / CERTIFY-WITH-FIXES / REWORK.
