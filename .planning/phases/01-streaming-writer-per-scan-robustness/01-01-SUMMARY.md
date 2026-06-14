---
phase: 01-streaming-writer-per-scan-robustness
plan: 01
subsystem: infra
tags: [parquet.net, streaming, zip-stored, fault-tolerance, mzpeak, row-groups]

requires:
  - phase: v1-point-layout (archived)
    provides: certified mzPeak point-layout writer (schemas, footer KV, cv_list, validator 0/0 bar)
provides:
  - MzPeakParquet multi-row-group streaming handle (OpenAsync -> WriteRowGroupAsync* -> CloseAsync(finalMetadata)) over a seekable sink
  - MzPeakSpectrumWriter streams spectra_data/spectra_peaks/chromatograms_data to seekable temp files then 64KB-bounded CopyTo into STORED zip entries
  - per-scan all-or-nothing stage-then-commit (incl. shared precursor-resolution map) — bad scans skip cleanly, no ordinal/row/precursor poisoning
  - injectable RowGroupRowCap (prod default 1,048,576) and a shared chrom-data schema/prefix helper
affects: [chunked-layout, numpress, metadata-streaming, any phase converting large RAW files]

tech-stack:
  added: []
  patterns:
    - "Streaming Parquet facet = one ParquetWriter + N row groups over a seekable temp FileStream; final footer KV set after the last row group, before dispose"
    - "STORED-zip assembly via bounded Stream.CopyTo from temp file (never whole-facet byte[])"
    - "Per-scan stage-into-locals then atomic commit; read/build failures skip, write/flush failures propagate"

key-files:
  created: []
  modified:
    - ThermoRawFileParser/Writer/MzPeak/MzPeakParquet.cs
    - ThermoRawFileParser/Writer/MzPeakSpectrumWriter.cs
    - ThermoRawFileParser/ThermoRawFileParser.csproj
    - ThermoRawFileParser/ThermoRawFileParserTest/MzPeakParquetTests.cs
    - ThermoRawFileParser/ThermoRawFileParserTest/MzPeakWriterTests.cs

key-decisions:
  - "chromatograms_data is STREAMED (uniform with the point facets) via ChromDataFacetStream; RegisterChromDataPrefixes() runs before BuildMetadataFacet so cv_list stays complete"
  - "RowGroupRowCap production default kept at 1,048,576 (matches ParquetSpectrumWriter.ParquetSliceSize); injectable via internal static TestRowGroupRowCap seam"
  - "_precursorScanNumbers[filterKey]=scanNumber moved into the success-only commit block (S2)"
  - "final-metadata mechanism is CloseAsync(IReadOnlyDictionary finalMetadata) on the streaming Handle"
  - "metadata facets remain buffered (bounded by spectrum count), per CONTEXT scope"

patterns-established:
  - "Streaming facet handle: Open(seekable) -> WriteRowGroup(slice)* -> Close(finalKV) -> dispose temp stream -> AddStoredFromFile -> delete temp in finally"
  - "All-or-nothing per scan: nothing shared mutates until full success; commit-block writes sit outside the skip-catch"

requirements-completed: [MEM-01, MEM-02, MEM-03, ROB-01, ROB-02]

duration: 75min
completed: 2026-06-14
---

# Phase 1 Plan 01: Streaming Writer + Per-Scan Robustness Summary

**Bounded-memory mzPeak writer: data facets stream to seekable temp files in 1,048,576-row Parquet row groups then 64KB-CopyTo into STORED zip entries, with per-scan all-or-nothing commit (incl. the precursor-resolution map) so bad scans skip without poisoning output — v1 logical output byte-semantically unchanged (validator 0/0).**

## Performance

- **Duration:** ~75 min
- **Started:** 2026-06-14T17:30Z
- **Completed:** 2026-06-14T17:55Z
- **Tasks:** 4
- **Files modified:** 5

## Accomplishments
- `MzPeakParquet.Handle` streaming API: `OpenAsync` asserts `CanSeek` (structurally blocks the silent in-RAM buffering trap), `WriteRowGroupAsync` emits N row groups read back as one logical table, `CloseAsync(finalMetadata)` lands the footer KV after the last row group.
- Three DATA facets converted from full in-memory `List<>` accumulation to bounded row-group streaming into temp `FileStream`s; finalize streams each temp file into its STORED zip entry via a 64 KB bounded `CopyTo` and deletes it in `finally`.
- Per-scan stage-then-commit: every read/build lands in locals; a read/build failure logs + `NewError()` + `continue` (no ordinal, no rows, no precursor-map entry); the commit block (incl. `_precursorScanNumbers`) applies atomically only on full success and a write/flush failure there propagates (FATAL) rather than masquerading as a skip.
- MEM-02 met on the 954 MB Orbitrap (validates 0/0); 8.4 GB Astral stretch converted and `--quick` validated.
- Full suite green native arm64: **60/60** (52 pre-existing + 8 new).

## Task Commits

1. **Task 1: MzPeakParquet streaming API + seekable-sink guard** - `c56b472` (feat, TDD)
2. **Task 2: Stream the DATA facets (temp file -> STORED zip), shared chrom-data helper** - `048cc1b` (feat, TDD)
3. **Task 3: Per-scan all-or-nothing precursor-map commit (S2) + robustness tests** - `6b5640c` (feat)
4. **Task 4: Full suite green + MEM-02 measurement** - no code change required (measurement + validation only); recorded below.

## Files Created/Modified
- `ThermoRawFileParser/Writer/MzPeak/MzPeakParquet.cs` - additive `OpenAsync`/`Handle.WriteRowGroupAsync`/`Handle.CloseAsync`; `WriteAsync` untouched.
- `ThermoRawFileParser/Writer/MzPeakSpectrumWriter.cs` - streaming `PointFacetStream`/`ChromDataFacetStream`, `AddStoredFromFile`, `RowGroupRowCap`/`TestRowGroupRowCap`, `RegisterChromDataPrefixes`, per-scan stage-then-commit, `PointFooter`; removed dead `BuildPointFacet`/`BuildChromatogramDataFacet`.
- `ThermoRawFileParser/ThermoRawFileParser.csproj` - `InternalsVisibleTo("ThermoRawFileParserTest")` for the cap seam.
- `ThermoRawFileParser/ThermoRawFileParserTest/MzPeakParquetTests.cs` - multi-row-group round-trip, final-metadata-after-row-groups, non-seekable-sink rejected.
- `ThermoRawFileParser/ThermoRawFileParserTest/MzPeakWriterTests.cs` - data-facet multi-row-group via real flush path, small.RAW v1 invariants, nine chrom-data accessions, precursor-isolation unit test, gated S4 bad-scan test.

## MEM-02 Peak-RSS Measurement (`/usr/bin/time -l`, native arm64)

| File | Size | Spectra | Data points | Peak points | max RSS | peak footprint | Validate |
|------|------|---------|-------------|-------------|---------|----------------|----------|
| small.RAW (CLI sanity) | ~1 MB | 48 | 305,213 | 12,890 | n/a | n/a | **0/0 PASS** |
| 70JG_02.raw (MEM-02 gate) | 954 MB | 53,012 | 213,635,692 | 15,015,914 | **2,136,637,440 B (≈2.04 GB)** | 1,213,253,864 B (≈1.13 GB) | **0/0 PASS (full)** |
| Astral (stretch) | 8.4 GB | 307,588 | 609,374,665 | 7,552,933 | 10,910,924,800 B (≈10.16 GB) | 1,835,131,472 B (≈1.71 GB) | **0/0 PASS (--quick)** |

**Budget / interpretation (Claude's discretion, documented):** the qualitative MEM-01 gate is met — point-data memory is bounded by ~one row-group buffer per open facet (3 × ~1M rows × ~20 B ≈ 60 MB), independent of the 213M/609M total point count. v1 would have held the full point lists (≈4–12 GB) plus whole-facet `byte[]`s (another ≈4–12 GB) and OOM-ed on the Astral. The residual RSS that DOES grow (2.0 GB at 53k spectra → 10.2 GB at 308k spectra) is driven by the **buffered metadata** dimension (`records` list + the metadata-facet column arrays built at finalize), which scales with spectrum count, NOT point count. Metadata streaming is explicitly out of scope this phase (CONTEXT) and is the clear MEM lever for the next phase. `RowGroupRowCap` left at the 1,048,576 production default — the point buffers are not the RSS bottleneck, so lowering it would not help.

## Decisions Made
- **chromatograms_data streamed** (not left buffered) for uniformity with the point facets via `ChromDataFacetStream`; `RegisterChromDataPrefixes()` is called unconditionally before `BuildMetadataFacet` so all nine chrom-data CURIE prefixes reach `cv_list` regardless.
- **RowGroupRowCap = 1,048,576** production default (matches `ParquetSpectrumWriter.ParquetSliceSize`); test seam is `internal static int? TestRowGroupRowCap` read via the `Cap` property the flush path actually uses.
- **`_precursorScanNumbers` commit placement:** now in the success-only commit block (S2), alongside `scanNumberToOrdinal` and `ordinal++`.
- **Final-metadata mechanism:** `Handle.CloseAsync(finalMetadata)` (assigns `writer.CustomMetadata` then disposes), not a settable property.
- **Metadata-buffered confirmed:** `spectra_metadata`/`chromatograms_metadata` and `BuildIndex`/`AddMetadataBlocks` are unchanged from v1.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking] Added `InternalsVisibleTo` to the main project**
- **Found during:** Task 2 (injectable RowGroupRowCap seam)
- **Issue:** the test must set the cap to force ≥2 row groups through the real flush path; the writer is constructed internally inside `RawFileParser.Parse`, so the cap cannot be passed via the public API.
- **Fix:** `<InternalsVisibleTo Include="ThermoRawFileParserTest" />` plus an `internal static int? TestRowGroupRowCap` seam (production default unchanged).
- **Committed in:** `048cc1b` (Task 2 commit)

---

**Total deviations:** 1 auto-fixed (1 blocking). No scope creep — production default and public surface unchanged.

## Issues Encountered

**ROB corpus file is fully unreadable, not a single-bad-scan file (genuine finding — gate NOT weakened).**
The plan's ROB gate expects `S4_5foldGHRP.raw` to "fail on scan #25081, then complete with a valid archive (skip + parity)". In reality this file is a Finnigan RAW (header `0x01a1 "Finnigan"`) whose scan-event index is corrupt: **every** scan 1..25081 raises `Cannot get scan event for N`. The robustness path handled it exactly as designed — no unhandled throw, all 25,081 skips logged and `NewError()`-counted, conversion completes cleanly with a "No in-range spectrum to write" (no corrupt partial archive). But with zero readable scans it produces **no** output, so the archive-parity-after-skip assertion cannot be exercised against this file.

Resolution (no fabrication, no weakened gate):
- The authoritative ROB-02 proof is the **deterministic precursor-isolation unit test** (no corpus dependency): a parent that fails after computing its filterKey writes nothing to `_precursorScanNumbers`, so a later child resolves to the *next valid* parent and the skipped scan consumes no ordinal — facet/metadata parity and dense ordinals hold by construction.
- The gated S4 integration test asserts the contract this file *can* demonstrate (no-abort + error-count ≥ 25,000) and additionally asserts parity + dense ordinals **when any scan is readable**, so it tightens automatically if a partially-corrupt Thermo file is dropped in later.

**Action for the phase owner:** to fully exercise the "valid archive with a genuine isolated skip" path, a Thermo RAW with a *single* unreadable scan among readable ones is needed; the current corpus file does not provide that. ROB-01/02 *behavior* is proven; only the corpus-archive-parity demonstration is blocked by the file's total corruption.

## Next Phase Readiness
- Streaming foundation is in place; point-data memory is constant w.r.t. point count, validator 0/0 preserved.
- **Primary next-phase lever:** stream the metadata facets — `records` + the metadata-facet column arrays are now the dominant RSS term (scales with spectrum count: 2.0 GB @ 53k → 10.2 GB @ 308k spectra).
- Chunked layout / Numpress can build directly on `MzPeakParquet.Handle` (row-group streaming) and `PointFacetStream` (temp-file + STORED assembly).
- Optional: source a Thermo RAW with a single isolated bad scan to close the corpus-archive-parity demonstration for ROB.

## Self-Check: PASSED
- SUMMARY.md present.
- Task commits c56b472, 048cc1b, 6b5640c all present in git log.
- Artifacts verified: `OpenAsync` in MzPeakParquet.cs, `AddStoredFromFile` in MzPeakSpectrumWriter.cs, `MultiRowGroup` test in MzPeakParquetTests.cs.
- Full suite 60/60 green native arm64; small.RAW + 70JG_02.raw validate 0/0; Astral --quick PASS.

---
*Phase: 01-streaming-writer-per-scan-robustness*
*Completed: 2026-06-14*
