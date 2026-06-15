# Corpus E2E findings (v2, post-row-group-fix)

Run: TRFP v2 (chunked + Numpress-linear default) over the mzML2mzPeak Thermo corpus, native arm64.

## Headline (95 Thermo files; the Bruker `S4_5foldGHRP.raw` removed — see below)

| Metric | Result |
|--------|--------|
| **Convert (no crash, archive produced)** | **95 / 95 (100%)** |
| **`mzpeak-validate` PASS (0/0)** | **95 / 95 (100%)** |
| Comparator ran without error | 94 / 95 (1 timeout — harness limitation, not the writer) |
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

## Remaining harness limitation (VER2-04, Phase 5)
`compare_mzpeak.py` is pure-Python and CPU-bound; it timed out (>900 s) on the 2.1 GB file. The
`iter_batches` change fixed its *memory* (no more COMPARE_ERROR from OOM/unreadability), not its *speed*.
Future: vectorize decode/compare with NumPy or compare at the Arrow level.

## Stage timing (measured)
- **Convert (the writer):** linear ~49 ms/MB (~20 MB/s, R²=0.985). Not a bottleneck.
- **Validate:** flat ~1.5 s (structure check). Not a bottleneck.
- **Compare:** ~2.7 ms/spectrum, pure-Python — dominates large-file wall-clock (~88% on a 160 MB file).
