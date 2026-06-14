---
phase: 02-chunked-layout
plan: 01
subsystem: writer
tags: [mzpeak, parquet, chunked, delta-encoding, streaming, parquet-net]

# Dependency graph
requires:
  - phase: 01-streaming-writer-per-scan-robustness
    provides: streaming Parquet Handle (OpenAsync/WriteRowGroupAsync/CloseAsync), NestedLevels/ListOf nested-level computer, PointFacetStream pattern, footer KV + cv_list machinery
provides:
  - chunked spectra_data layout (6-field reference chunk struct, delta-encoded m/z) as the new default
  - null-aware delta codec (MzPeakChunkCodec) that reads reference null-marked encodings
  - fixed m/z-window chunker mirroring null_chunk_every_k
  - --point (v1 point layout) and --chunk-size CLI flags
  - chunk spectrum_array_index footer locked verbatim against the reference
affects: [03-numpress, 04-zrs-null-marking, 05-chromatogram-chunking]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "ISpectraDataFacet: layout-agnostic facet contract selected by --point; chunk vs point share Append/Close/TempPath/PointCount"
    - "Chunk rows streamed through the Phase-1 Handle as nullable list<double>/list<float> leaves via NestedLevels/ListOf (row-group cap counts chunk rows)"
    - "Codec is null-aware in STRUCTURE (reads reference [None,absolute,delta] / [None,None]) but the writer path stays null-free"

key-files:
  created:
    - ThermoRawFileParser/Writer/MzPeak/MzPeakChunkCodec.cs
    - ThermoRawFileParser/ThermoRawFileParserTest/MzPeakChunkTests.cs
  modified:
    - ThermoRawFileParser/Writer/MzPeakSpectrumWriter.cs
    - ThermoRawFileParser/ParseInput.cs
    - ThermoRawFileParser/MainClass.cs
    - ThermoRawFileParser/ThermoRawFileParserTest/MzPeakWriterTests.cs
    - ThermoRawFileParser/ThermoRawFileParserTest/MzPeakDifferentialTests.cs

key-decisions:
  - "m/z delta round-trip is BIT-EXACT on real Thermo m/z (empirically 48/48 spectra, 305213/305213 points, 0 m/z-bit and 0 intensity-bit mismatches) -> exact/L1 losslessness claim holds unconditionally; NO tolerance needed"
  - "chromatograms_data stays POINT as a deliberate documented deviation; the reference CHUNKS it -> full chromatogram chunking deferred to Phase 5"
  - "chunk struct is exactly 6 fields (no mz_numpress_linear_bytes); chunk_encoding = MS:1003089 (CURIE, not 'delta'); intensity item f32"

patterns-established:
  - "Layout switch via ISpectraDataFacet + ParseInput.MzPeakPointLayout; v1 tests opt into --point because chunked is now the default"
  - "Footer/transform CURIEs copied verbatim from a pyarrow dump of the reference, locked by an automated footer-parse test"

requirements-completed: [CHUNK-01, CHUNK-02, CHUNK-03, CHUNK-04, CHUNK-05, CHUNK-06]

# Metrics
duration: 15min
completed: 2026-06-14
---

# Phase 2 Plan 01: Chunked Layout Summary

**spectra_data now emits the reference 6-field chunk struct (delta-encoded m/z) as the default, with --point restoring the v1 point layout; the delta round-trip is empirically BIT-EXACT on real Thermo m/z so losslessness is exact/L1 with no tolerance, and both modes validate at 0 errors.**

## Performance

- **Duration:** ~15 min
- **Started:** 2026-06-14T18:15:37Z
- **Completed:** 2026-06-14T18:30:03Z
- **Tasks:** 3 completed
- **Files modified:** 7 (2 created, 5 modified)

## Accomplishments
- Null-aware delta codec + fixed m/z-window chunker mirroring null_delta_encode/decode/null_chunk_every_k, with empty / all-equal / single-point / non-monotonic / boundary-singleton guards and a reference null-decode path.
- ChunkFacetStream emits the exact reference 6-field chunk struct through the Phase-1 streaming Handle (nullable list leaves via NestedLevels/ListOf); chunked is the new default, --point restores v1, --chunk-size configures the window (default 50.0).
- Empirically established that f64 delta encode+reconstruct is BIT-EXACT on the real Thermo m/z in small.RAW (CHUNK-06 exact/L1), and locked it with a bitwise multiset test.

## Task Commits

Each task was committed atomically on main:

1. **Task 1: null-aware delta codec + window chunker** - `e5c036b` (feat, TDD: codec + 6 unit tests in one commit)
2. **Task 2: chunked spectra_data via streaming Handle; --point/--chunk-size** - `01092ad` (feat)
3. **Task 3: verification locks + footer parity + v1 point opt-in** - `608f30d` (test)

## Files Created/Modified
- `ThermoRawFileParser/Writer/MzPeak/MzPeakChunkCodec.cs` - DeltaEncode/DeltaDecode (null-aware, absolute-restart-after-null) + Chunk (first-mz-anchored window, boundary-singleton roll-in, non-monotonic guard).
- `ThermoRawFileParser/Writer/MzPeakSpectrumWriter.cs` - ChunkStructField (6 fields), ChunkedSpectrumArrayIndex footer const, ChunkFacetStream, ISpectraDataFacet, ChunkFooter, layout switch + cv_list CURIE registration.
- `ThermoRawFileParser/ParseInput.cs` - MzPeakPointLayout (default false=chunked), MzPeakChunkSize (default 50.0).
- `ThermoRawFileParser/MainClass.cs` - `--point` and `--chunk-size=` options (InvariantCulture parse, non-positive falls back to 50.0).
- `ThermoRawFileParser/ThermoRawFileParserTest/MzPeakChunkTests.cs` - 6 codec unit tests + 6 integration locks (schema diff, footer lock, bitwise multiset, validator 0/0, size shrink, peaks+chrom unchanged).
- `ThermoRawFileParser/ThermoRawFileParserTest/MzPeakWriterTests.cs` / `MzPeakDifferentialTests.cs` - v1 fixtures opt into --point (default flipped to chunked).

## Decisions Made

### Empirical m/z losslessness finding (CHUNK-06) — BIT-EXACT
The decisive question was whether f64 delta encode (`mz[i]-mz[i-1]`) + reconstruct (`mz[i-1]+delta`) is bit-exact on real Thermo m/z, or only sub-ULP. Measured on OUR small.RAW output (chunked-decode vs --point, bitwise keys BitConverter.DoubleToInt64Bits / SingleToInt32Bits, NO rounding):

```
chunk pts=305213  point pts=305213
spectra: 48/48 MATCHED bitwise   MISMATCH=0   mz-bit-mismatch=0   int-bit-mismatch=0
```

Result: **bit-exact**. max |delta| = 0 / ULP distance = 0 across all 305,213 points. The exact/L1 claim holds **unconditionally** — no tolerance was set, no bounded/sub-ULP reframing needed. Intensity is never delta-encoded and is bit-exact by construction (it is the verbatim slice). The reference file showed the same (14/14, 0 mismatches), which predicted this outcome.

### Literal CURIEs used (verbatim from refs/mzPeak/small.chunked.mzpeak)
- `chunk_encoding` column value = **MS:1003089** (the delta CURIE; NOT the literal "delta").
- m/z transform (chunk_start/end/values/encoding footer entries) = **MS:1003901**.
- intensity transform (chunk_secondary footer entry) = **MS:1003902**.
- Footer prefix `chunk`, 5 entries, buffer_formats `[chunk_start, chunk_end, chunk_values, chunk_encoding, chunk_secondary]`, m/z entries `sorting_rank:0`, intensity entry `sorting_rank:null`. cv_list registers MS:1003089/MS:1003901/MS:1003902.

### Chromatogram-point deviation (P1)
The authoritative reference `refs/mzPeak/small.chunked.mzpeak` emits `chromatograms_data` in CHUNK layout (time_chunk_start/end/values, chunk_encoding=MS:1003089, intensity, ms_level). Phase 2 deliberately keeps `chromatograms_data` POINT: the validator has no chromatogram structural rule and the TIC is tiny, so chunking yields negligible benefit. **The earlier "the reference keeps chromatograms point" claim is false and was not propagated.** Full chromatogram chunking for structural parity is deferred to Phase 5.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] Chunk intensity footer entry omitted sorting_rank**
- **Found during:** Task 3 (Footer_ArrayIndex_Lock)
- **Issue:** The initial ChunkedSpectrumArrayIndex const omitted the `sorting_rank` key on the intensity (chunk_secondary) entry; the reference footer carries `"sorting_rank": null` explicitly, and the footer-parse test threw NullReferenceException reading the absent key.
- **Fix:** Added `"sorting_rank":null` to the intensity entry so the footer matches the reference verbatim.
- **Files modified:** ThermoRawFileParser/Writer/MzPeakSpectrumWriter.cs
- **Verification:** Footer_ArrayIndex_Lock passes; pyarrow dump confirms our footer == reference footer entry-for-entry.
- **Committed in:** 608f30d

**2. [Rule 3 - Blocking] v1 point-layout fixtures broke when the default flipped to chunked**
- **Found during:** Task 3 (full-suite run)
- **Issue:** 12 v1 tests in MzPeakWriterTests/MzPeakDifferentialTests read `spectra_data` as the point struct via the default ParseInput, which now produces chunked output.
- **Fix:** v1 point-layout fixtures opt into the v1 path: MzPeakWriterTests.Convert and WriteWithInjection set MzPeakPointLayout=true; the Differential CLI conversion adds `--point`. No v1 assertion was weakened; the point path itself is unchanged.
- **Files modified:** MzPeakWriterTests.cs, MzPeakDifferentialTests.cs
- **Verification:** Full suite 72/72 green.
- **Committed in:** 608f30d

---

**Total deviations:** 2 auto-fixed (1x Rule 1, 1x Rule 3)
**Impact on plan:** Both necessary for correctness/no-regression. Deviation 1 makes the footer byte-for-byte match the reference; deviation 2 preserves v1 invariants under the new default. No scope creep.

## Issues Encountered
None beyond the deviations above. The empirical losslessness question resolved cleanly to bit-exact.

## Verification Results
- **Build:** clean native arm64 Release (0 errors).
- **Tests:** full suite 72/72 passing native arm64 (12 new chunk tests + 60 pre-existing, 0 regressions).
- **Schema diff:** our chunked spectra_data field names + item types == reference (6 fields; mz item double, intensity item float; chunk_encoding == MS:1003089).
- **Footer lock:** prefix `chunk`, buffer_formats + transform CURIEs (MS:1003901 x4, MS:1003902) + m/z sorting_rank:0 == reference footer; --point retains the v1 point index.
- **Losslessness (CHUNK-06):** chunked-decode == --point BITWISE per spectrum — intensity bit-exact, m/z bit-exact (0 mismatches; exact/L1, no tolerance).
- **Validator:** 0 error-level findings on BOTH chunked and --point archives of small.RAW.
- **Size:** chunked spectra_data.parquet 1,879,809 B < point 2,231,850 B (~15.8% smaller) for small.RAW.
- **Peaks/chromatograms:** both stay point in chunked mode (chromatograms = deliberate Phase-2 deviation).

## Next Phase Readiness — what Phase 3 (Numpress) needs
- **The 6-field chunk struct is the plug point.** Phase 3 adds the `mz_numpress_linear_bytes` list<uint8> field (7th field) ONLY when chunk_encoding is the NumpressLinear CURIE; in delta mode the column is ABSENT (verified: `ChunkingStrategy::Delta::extra_arrays() -> vec![]`). Do not add it present-but-empty.
- **chunk_encoding becomes the encoding selector:** delta = MS:1003089 (lossless, current default); Numpress-linear = its own CURIE (lossy m/z, recorded). Register the new CURIE in cv_list and the footer transform.
- **The codec's null-aware decode path already reads reference/Phase-4 null markers**, so Phase 4 (ZRS null-marking) can enable zero-run nulling in the writer without changing the decoder.
- **ChunkFacetStream / ISpectraDataFacet are the integration seam**; encoding choice should branch inside DeltaEncode-equivalent + the chunk_encoding value, not fork the facet.
- Chromatogram chunking (full reference parity) is queued for Phase 5.

## Self-Check: PASSED
- Created files exist: MzPeakChunkCodec.cs, MzPeakChunkTests.cs, 02-01-SUMMARY.md.
- Commits exist: e5c036b (Task 1), 01092ad (Task 2), 608f30d (Task 3).

---
*Phase: 02-chunked-layout*
*Completed: 2026-06-14*
