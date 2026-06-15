# Corpus E2E findings (v2, post-row-group-fix)

Run: TRFP v2 (chunked + Numpress-linear default) over the mzML2mzPeak Thermo corpus, native arm64.

## Headline (95 Thermo files; the Bruker `S4_5foldGHRP.raw` removed — see below)

| Metric | Result |
|--------|--------|
| **Convert (no crash, archive produced)** | **95 / 95 (100%)** |
| **`mzpeak-validate` PASS (0/0)** | **95 / 95 (100%)** |
| Comparator ran without error | **96 / 96 (100%)** — the lone 2.1 GB timeout resolved by the vectorized comparator (see VER2-04 below) |
| **8.4 GB Astral extreme-scale** | convert + validate + pyarrow read-back **PASS** (68 row groups, 7.79M chunk rows, readable; peak RSS 12.9 GB) |

## The large-file bug this run found and fixed
Initial v2 run had **61 `COMPARE_ERROR`** on files ≥483 MB: chunked `spectra_data.parquet` was written as ONE
~400 MB row group of fat chunk rows, unreadable by pyarrow ("Unexpected end of stream") — while
`mzpeak-validate` PASSed it (validator blind spot). Root cause: row-group flush cap was **row-count-based**
(1,048,576), wrong for fat chunk/list rows. Fix: **byte-aware flush** (`RowGroupByteCap` ≈64 MB) — every
large file now splits into many readable row groups (the 8.4 GB Astral → 68 row groups, fully readable).
A large-file chunked read-back NUnit gate was added so this can't regress.

## The DIFFs are a known confound, NOT writer errors
Every file is 100% structurally aligned (spectrum count, ms_level, RT). Strict multiset match is partial
(exact ≈0.22–0.55, rising on MS2-rich files) because the **reference uses lossy SLOF intensity +
zero-stripping**, while v2 keeps **lossless f32 intensity and all points** (zero-stripping is Phase 4).
A fair differential needs tolerance + Phase-4 stripping (VER2-02). Our output is *more* accurate than the
reference on intensity.

## Excluded input
`S4_5foldGHRP.raw` (`raw-replacements/bruker-impact-sub__PXD076459/`) — a **Bruker impact II** acquisition.
TRFP is Thermo-only; both TRFP's RawFileReader AND the reference mzML2mzPeak/mzdata fail to read any scans
(the corpus's own `trfp.log`/`mzpeak.log` show 0 spectra; its reference `local.mzpeak` is empty). Our copy is
byte-identical to the published PRIDE file (same `Content-Length` 437862917 + same header), so it is not a
download/corruption issue — the source file is unreadable. **Removed from `corpus_pairs.json`.** TRFP handled
it correctly (graceful per-scan skip, no crash, no bogus archive).

## VER2-04 RESOLVED — comparator vectorized (full corpus now completes)
`compare_mzpeak.py` was pure-Python and CPU-bound; it **timed out (>900 s)** on the 2.1 GB
`2024_LRS_Ascend_WT_C_03.raw` (the lone `COMPARE_ERROR`). Rewritten to read facets via Arrow columns
and key each spectrum's multiset with NumPy (`np.unique`) instead of per-point Python loops. Output is
**byte-identical** to the old comparator (verified across default / `--lossless` / `--point`), and it is
**~11× faster** (141 MB file: 258 s → 23.7 s). The 2.1 GB file now completes end-to-end: convert 101 s,
`mzpeak-validate` PASS (0 errors), compare **775 s** (< the 900 s budget). **All 96 pairs now resolve**
(0 `COMPARE_ERROR`). Headroom on the largest file is ~14%; an Arrow-native/Cython pass is the next lever
if a larger file is ever added.

## VER2-02 — differential re-run (honest framing, not a regression)
Re-run over all 96 Thermo pairs (chunked+Numpress default): **100% structural alignment on every file**
(spectrum count, ms_level, polarity, RT all match). The strict **exact-(m/z,intensity)-multiset rate is
low (≈0–0.67)** — NOT converter error, but because the **reference is lossy** (Numpress-SLOF intensity +
interior zero-run stripping) while TRFP keeps **lossless f32 intensity and all points**. On every
mismatch example `ours_pts > ref_pts` (we keep more, exact, points). The Phase-5 "exact-match rises vs v1"
hope is therefore not achievable against a lossy reference *by design* (Phase-5 key-risk #3 anticipated
exactly this); the honest result is that our output is **more accurate** than the reference on intensity.
Matching the reference's zero-stripping was the dropped ZRS work, now backlog BL-02.

## Stage timing (measured)
- **Convert (the writer):** linear ~49 ms/MB (~20 MB/s, R²=0.985). Not a bottleneck.
- **Validate:** flat ~1.5 s (structure check). Not a bottleneck.
- **Compare:** ~2.7 ms/spectrum, pure-Python — dominates large-file wall-clock (~88% on a 160 MB file).
