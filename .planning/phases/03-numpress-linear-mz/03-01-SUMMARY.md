---
phase: 03-numpress-linear-mz
plan: 01
subsystem: writer
tags: [mzpeak, parquet, numpress, numpress-linear, compression, lossy-mz]

# Dependency graph
requires:
  - phase: 02-chunked-layout
    provides: chunk codec, ChunkFacetStream/ISpectraDataFacet seam, ChunkStructField/ChunkedSpectrumArrayIndex footer, --point/--chunk-size, cv_list registration, MzPeakParquet.NullList
  - phase: 01-streaming-writer-per-scan-robustness
    provides: streaming Parquet Handle (OpenAsync/WriteRowGroupAsync/CloseAsync), NestedLevels/ListOf nested-level computer
provides:
  - pure-managed AnyCPU C# MSNumpress-linear codec (OptimalLinearFixedPoint/EncodeLinear/DecodeLinear)
  - numpress-linear m/z chunk encoding as the new default (mz_numpress_linear_bytes, MS:1002312, mz_chunk_values null)
  - --no-numpress / --lossless (delta chunks) + --point (v1) opt-outs with lossy-m/z CLI warning + data_processing note
  - anchor-aligned numpress decode helper (MzPeakChunkCodec.NumpressDecode) dropping leading/trailing phantoms
affects: [04-zrs-null-marking, 05-chromatogram-chunking]

# Tech tracking
tech-stack:
  added:
    - "vendored ms-numpress canonical linear codec, ported line-for-line to managed C# (Apache-2.0)"
  patterns:
    - "Encoding selected INSIDE ChunkFacetStream by a numpress flag (no forked facet); 7th list<uint8> field present only in numpress mode"
    - "int64 accumulation in encode/decode mirrors the reference value reconstruction so m/z*fp values exceeding 2^32 decode correctly (canonical uses unsigned wraparound; int64 avoids the divide-after-wrap bug)"
    - "Footer/struct/cv_list/data_processing all branch on the same numpress flag; delta + point paths untouched"

key-files:
  created:
    - ThermoRawFileParser/Writer/MzPeak/MSNumpress.cs
  modified:
    - ThermoRawFileParser/Writer/MzPeak/MzPeakChunkCodec.cs
    - ThermoRawFileParser/Writer/MzPeakSpectrumWriter.cs
    - ThermoRawFileParser/ParseInput.cs
    - ThermoRawFileParser/MainClass.cs
    - ThermoRawFileParser/ThermoRawFileParserTest/MzPeakChunkTests.cs

key-decisions:
  - "numpress chunk_encoding AND mz-bytes transform = MS:1002312 (verified pyarrow dump); ROADMAP's MS:1003089 is the DELTA encoding — documented override applied"
  - "m/z-only numpress (Option B): intensity stays plain f32 (no SLOF); footer has 6 entries (m/z anchors + intensity verbatim + numpress bytes), NOT the reference's 8th intensity-SLOF field"
  - "MSNumpress ported from the canonical reference source, NOT RESEARCH-port.md prose (whose least-squares-slope / 5-byte-value description is wrong); 4-byte int32 first values + encodeInt half-byte residuals confirmed byte-identical to pynumpress"

requirements-completed: [NP-01, NP-02, NP-03, NP-04]

# Metrics
duration: ~50min
completed: 2026-06-14
---

# Phase 3 Plan 01: Numpress-Linear m/z Summary

**A faithful pure-managed C# MSNumpress-linear codec (byte-identical to canonical ms-numpress / pynumpress) now encodes each chunk's m/z into `mz_numpress_linear_bytes` (MS:1002312) as the new default, with `mz_chunk_values` null and intensity kept lossless f32; `--lossless` restores delta chunks and `--point` the v1 layout — all three validate 0/0 and numpress is the smallest.**

## Performance
- **Tasks:** 4 completed, one atomic commit each on `main`.
- **Files:** 1 created (MSNumpress.cs), 5 modified.

## Accomplishments
- Vendored a line-for-line managed port of canonical ms-numpress linear (OptimalLinearFixedPoint, encode/decodeInt half-byte scheme, encode/decodeLinear, BE fixed-point prefix), AnyCPU (System only, no x64).
- Numpress is the default chunk encoding: m/z → `mz_numpress_linear_bytes` (per-chunk optimal fixed point), `mz_chunk_values` NULL via `NullList` (not EmptyList), `chunk_encoding = MS:1002312`, intensity unchanged plain f32.
- 6-entry numpress footer (Option B), cv_list registers MS:1002312, data_processing records the numpress transform + a lossy-m/z note.
- `--no-numpress`/`--lossless` → delta chunks (MS:1003089); `--point` → v1 point; precedence point > lossless > numpress-default; lossy-m/z CLI warning only in effective numpress mode.

## Port verification (vectors)
- **V1** `OptimalLinearFixedPoint([1,2,3]) == 1073741823` (floor(0x7FFFFFFF/2)).
- **V2** `EncodeLinear([100.0,100.5,101.0,101.5], fp=21367996)` == `41 74 60 CB C0 00 00 00 70 F9 5C 7F CE FF FF 7F 88` (hardcoded literal); exact decode.
- **V3** reference `small.numpress.mzpeak` row-0 first-8-bytes BE double == `10599266.0`; anchored decode within 0.5/fp of both `mz_chunk_start` and `mz_chunk_end` (this row carries a TRAILING phantom — handled by the anchor-align helper).
- **V4** byte-identity vs the ms-numpress known vector AND vs `pynumpress.encode_linear` at a shared fp (asserted twice). Plus: pynumpress decode of our bytes within 0.5/fp and OptimalLinearFixedPoint parity with `pynumpress.optimal_linear_fixed_point` on real m/z.
- Round-trip within 0.5/fp for n ∈ {3,17,251,1024}; empty(8B)/single(12B)/two(16B) edges; AnyCPU/MSIL assembly assertion green. Single-value 12-byte chunk decoded with OUR codec as oracle (pynumpress 0.0.9 rejects it).

## m/z-only-numpress decision + size delta (small.RAW spectra_data.parquet)
| mode | spectra_data.parquet | total archive |
|------|---------------------:|--------------:|
| numpress (default) | **1,523,724** | 1,648,853 |
| --lossless (delta) | 1,879,829 | 2,004,075 |
| --point (v1) | 2,231,850 | 2,356,096 |

numpress is **18.9% smaller** than delta-chunked spectra_data (the m/z-only saving; the reference's ~63% requires intensity SLOF, deferred to backlog). Ordering numpress < lossless < point holds.

## 3-mode validator results
`mzpeak-validate` (profile mzpeak-0.9): **0 errors** on small.RAW in numpress (default), `--lossless`, and `--point`.

## L2 bound measured
Decoded numpress m/z compared positionally to `--lossless` (Phase-2 proven bit-exact to source): per-chunk decoded length == intensity == lossless length; total compared == `spectrum_data_point_count` (305,213); both start and end anchors within 0.5/fp; **worst |Δm/z| ≈ 1.6e-7 Th** (well under the ~5e-7 worst-case bound). Intensity is **bit-exact f32** (0 mismatches) in numpress mode; `--lossless` and `--point` remain BITWISE-L1.

## Deviations from Plan
### Auto-fixed Issues
**1. [Rule 1 - Bug] int32 wraparound in numpress decode/accumulation**
- **Found during:** Task 1 (round-trip n=1024).
- **Issue:** The canonical algorithm uses unsigned 32-bit `ints`; a literal uint32 port wraps `2*ints[1]-ints[0]` once m/z*fp exceeds 2^32 and then divides the wrapped value, corrupting all values past that point. pynumpress avoids this by accumulating in a wider integer before the float divide.
- **Fix:** Accumulate `ints` in `int64` in both EncodeLinear and DecodeLinear (the on-wire diff stays the low-32-bit `int` so byte output remains identical to pynumpress). Verified byte-identity preserved and round-trip clean to n=4000.
- **Files:** MSNumpress.cs. **Commit:** f1bfaa1.

**2. [Rule 1 - Bug] DecodeInt nibble placement**
- **Found during:** Task 1.
- **Issue:** encodeInt stores the low (8-l) nibbles of the value; the first decode pass placed read nibbles at positions n..7 instead of 0..(7-n).
- **Fix:** Read (8-n) nibbles into positions 0..(7-n); seed all-ones for the leading-ones (head>8) case.
- **Files:** MSNumpress.cs. **Commit:** f1bfaa1.

**3. [Rule 2 - Critical] L2 anchor float slack**
- **Found during:** Task 4.
- **Issue:** The end-anchor check (exact source m/z vs quantized decode) exceeded the strict `0.5/fp` by ~2e-15 due to round-half quantization + float division.
- **Fix:** Allow `(0.5/fp)*(1+1e-6)` slack on anchor checks; the independent `worstAbs <= 5e-7` global bound (with large headroom) remains strict.
- **Files:** MzPeakChunkTests.cs. **Commit:** 8137004.

**Deviation vs ROADMAP CURIE:** ROADMAP NP-02 says MS:1003089 for numpress; the verified reference dump shows numpress = **MS:1002312** (MS:1003089 is delta). MS:1002312 used and documented.

## Known Stubs
None. mz_chunk_values is intentionally NULL in numpress mode (the m/z lives in mz_numpress_linear_bytes); this is the reference layout, not a stub.

## Self-Check: PASSED
- MSNumpress.cs exists; commits f1bfaa1, f387d50, 47dc5ad, 8137004 present on main.
- Full NUnit suite 86/86 green native arm64.

## Next Phase Readiness — what Phase 4 (ZRS null-marking) needs
- The numpress branch lives inside ChunkFacetStream keyed on the numpress flag; ZRS zero-run null-marking applies to the delta (`--lossless`) m/z values path and/or intensity — numpress carries raw bytes, so ZRS would interact with the lossless/intensity columns, not the numpress bytes.
- `MzPeakChunkCodec.NumpressDecode` (anchor-aligned, short-chunk guarded) is the canonical reader-side helper for decoding our numpress output.
- Intensity-SLOF (Numpress short-logged-float) remains the deferred backlog item that would unlock the reference's ~63% headline; m/z numpress alone yields ~19% on small.RAW.

---
*Phase: 03-numpress-linear-mz*
*Completed: 2026-06-14*
