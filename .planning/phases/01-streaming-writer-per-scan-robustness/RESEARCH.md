# Phase 1 (v2): Streaming Writer + Per-Scan Robustness - Research

**Researched:** 2026-06-14
**Domain:** Parquet.Net 5.0.1 multi-row-group streaming, ZIP STORED packaging, per-scan fault tolerance in a Thermo RAW reader loop
**Confidence:** HIGH (every load-bearing API claim verified empirically against the installed Parquet.Net 5.0.1 + System.IO.Compression on this machine)

## Summary

The whole phase reduces to four mechanical changes inside `MzPeakSpectrumWriter.Write` plus one
additive method on `MzPeakParquet`. The hard unknowns — "can one `ParquetWriter` emit many row
groups read as one table?" and "does it need a seekable stream?" — are now **settled by running
code**, not docs: yes to multi-row-group, and yes to seekable. A `ParquetWriter` handed a
non-seekable stream does **not** throw; it silently buffers the entire facet in memory and flushes
on dispose — which would defeat the constant-memory goal invisibly. Therefore the temp-file strategy
in CONTEXT.md is not just convenient, it is the only way to get true streaming: write each data
facet to a seekable temp `FileStream` in bounded row groups, then bounded-`CopyTo` the finished file
into a STORED zip entry and delete the temp.

The per-scan robustness requirement is subtler than "add a try/catch" — the loop **already has
one** (`MzPeakSpectrumWriter.cs:142`). The real defect is that point data is appended to the
accumulators (lines 154-176) *before* the metadata record is finalized and `ordinal++` runs (line
225). An exception thrown after the append but before the finalize (the S4_5foldGHRP scan #25081
case: a later `GetScanStatsForScanNumber` / `BuildPrecursor` step fails) leaves orphan points under
that ordinal with no metadata row, and the same ordinal is reused by the next good scan — silently
breaking facet/metadata parity. The streaming rewrite must make each scan **all-or-nothing**: stage
a scan's rows, and only commit them (and the ordinal) once the whole scan succeeded.

**Primary recommendation:** Add `MzPeakParquet.OpenFacet(...)` returning a small streaming handle
(one `ParquetWriter` over a temp `FileStream`) with `WriteRowGroup(cols)` / `DisposeAsync`; in
`MzPeakSpectrumWriter` convert `spectra_data`, `spectra_peaks`, `chromatograms_data` to per-scan
**staged-then-committed** appends flushed at a 1,048,576-row cap; keep the three metadata facets
buffered exactly as today; at finalize, bounded-`CopyTo` each temp file into its STORED zip entry.

## Architectural Responsibility Map

| Capability | Primary Tier | Secondary Tier | Rationale |
|------------|-------------|----------------|-----------|
| Multi-row-group Parquet encoding | `MzPeakParquet` (Parquet I/O helper) | — | Single chokepoint for all low-level Parquet; streaming is an I/O concern, not a writer-policy concern |
| Row-group flush cadence / buffering | `MzPeakSpectrumWriter` (conversion driver) | `MzPeakParquet` (the temp-file sink) | The writer owns the per-scan loop and knows when a buffer is full and a scan is whole |
| Seekable target / temp-file lifecycle | `MzPeakSpectrumWriter` finalize | OS temp dir | Temp files are an archive-assembly detail local to the one `Write` call |
| STORED zip packaging | `MzPeakSpectrumWriter` (`AddStored` family) | `System.IO.Compression.ZipArchive` | Already owned here; only the *source* of bytes changes (temp file vs `byte[]`) |
| Per-scan fault isolation | `MzPeakSpectrumWriter` loop | `ParseInput.NewError`/`NewWarn` (counters) | Matches `MzMlSpectrumWriter`'s established pattern |

## User Constraints (from CONTEXT.md)

### Locked Decisions
- **Stream the unbounded (point/data) facets** — `spectra_data`, `spectra_peaks`,
  `chromatograms_data` — in bounded row groups: open one `ParquetWriter` per facet, accumulate a
  row-group buffer, flush at a cap (configurable rows or ~bytes), repeat, close at end. Peak memory =
  one row-group buffer per open facet, independent of total point count.
- **Seekable target via temp files.** Parquet needs a seekable stream (footer on close), but a
  STORED zip-entry stream is not seekable. Write each facet to a temp file (seekable, streamed row
  groups), then at finalize **stream-copy** each temp file into its STORED zip entry and delete it.
  Constant memory, bounded temp disk. (MEM-03)
- **Metadata facets may stay buffered for this phase.** `spectra_metadata` /
  `chromatograms_metadata` are bounded by spectrum count (not point count) — keep them in-memory in
  v1's form. State the chosen approach in the SUMMARY. (Keeps the co-resident packed-table writer
  untouched, reducing regression risk.)
- **`MzPeakParquet` gains a multi-row-group streaming path** (open → write row group → … → close) in
  addition to the existing one-shot `WriteAsync`. Existing point/chunk schema + column logic reused.
- **Per-scan robustness (ROB):** wrap each scan's read/append in try/catch; on failure, log + count
  via `ParseInput.NewError()`/`NewWarning()` and SKIP the scan (no rows in any facet, ordinal NOT
  consumed), mirroring `MzMlSpectrumWriter`. Conversion continues and still emits a valid archive.
  Skipped scans must leave data/metadata spectrum sets consistent (dense ordinals, facet parity).

### Must NOT regress (v1 conformance)
- Output still passes `mzpeak-validate` (0/0) and is byte-semantically identical to v1 point-layout
  output for a clean file (same spectra, same (m/z,intensity) multiset, same metadata/counts).
  Row-group chunking is an internal Parquet detail — readers see the same logical table.
- Facet parity: `spectrum_count` == distinct `spectrum_index` in data == metadata spectrum rows.

### Claude's Discretion
- Exact flush-cap value and whether to add a bytes-based secondary trigger (default: rows only).
- Whether the streaming handle is a nested type on `MzPeakParquet` or a small new class.
- Whether to also stream the metadata facets (deferred unless profiling shows they matter).
- Temp-file directory choice and naming.

### Deferred Ideas (OUT OF SCOPE)
- Chunked layout / Numpress (later phases — this phase keeps point layout).
- Streaming the metadata facets (defer unless profiling demands it).
- Any output-format change.

## Phase Requirements

| ID | Description | Research Support |
|----|-------------|------------------|
| MEM-01 | Convert large RAW in constant memory | Decision 1 (streaming row groups) + Decision 3 (temp-file sink); EXP1/forward-only test prove multi-row-group + that a seekable sink is mandatory for constant memory |
| MEM-02 | Bounded peak RSS on a large corpus file | Decision 6 (measurement via `/usr/bin/time -l`, max RSS confirmed on macOS); concrete 718 MB–954 MB Orbitrap corpus identified, 8.4 GB Astral stretch |
| MEM-03 | Bounded temp disk, no whole-facet in memory | Decision 2 (temp file → bounded `CopyTo` → STORED entry → delete); zip-entry test proves bounded copy + STORED |
| ROB-01 | Survive a single bad scan, emit valid archive | Decision 5 (all-or-nothing per-scan commit); `MzMlSpectrumWriter:422-433` pattern; S4 root-cause identified |
| ROB-02 | Skipped scan leaves parity intact (dense ordinals) | Decision 5 (stage-then-commit; ordinal only advances on full success) |

## Decisions Block (per unknown)

### Decision 1 — Parquet.Net 5.0.1 multi-row-group streaming: CONFIRMED

One `ParquetWriter` can create many row groups sequentially; readers see **one logical table**.
Verified empirically (EXP1): three `CreateRowGroup()` calls on a single writer produced a file with
`RowGroupCount == 3` and 9 total rows read back across the groups; pyarrow independently read 9 rows
/ 3 row groups. `[VERIFIED: ran Parquet.Net 5.0.1 on this machine, /tmp/pqexp]`

The pattern that mirrors the existing `WriteAsync` body:

```csharp
// MzPeakParquet streaming path (NEW) — open once, write N row groups, close to flush footer.
// seekableSink MUST be a seekable Stream (temp FileStream). See Decision 3.
public static async Task<ParquetWriter> OpenAsync(Stream seekableSink, ParquetSchema schema,
    IReadOnlyDictionary<string, string> customMetadata)
{
    var writer = await ParquetWriter.CreateAsync(schema, seekableSink).ConfigureAwait(false);
    writer.CompressionMethod = CompressionMethod.Zstd;               // same codec as WriteAsync
    if (customMetadata != null) writer.CustomMetadata = customMetadata;
    return writer;
}

// Call once per buffer flush. columns carries the SAME (defined, defLevels, repLevels) triples
// the existing Column() chokepoint already builds, just for a buffer-sized slice of rows.
public static async Task WriteRowGroupAsync(ParquetWriter writer, ParquetSchema schema,
    IDictionary<DataField, (Array defined, int[] defLevels, int[] repLevels)> columns)
{
    using (var rg = writer.CreateRowGroup())
        foreach (var field in schema.GetDataFields())
        {
            var t = columns[field];
            await rg.WriteColumnAsync(Column(field, t.defined, t.defLevels, t.repLevels)).ConfigureAwait(false);
        }
}
// Caller disposes the ParquetWriter at end-of-facet → footer flushed to the temp file.
```

**How the current `WriteAsync` differs (single row group):** `MzPeakParquet.cs:156-175` opens the
writer, creates **exactly one** `CreateRowGroup()`, writes every column once, and disposes. The
minimal extension is to split that into *open* (CreateAsync + codec + metadata) / *write-row-group*
(the inner `CreateRowGroup` loop, callable N times) / *close* (dispose the writer). `WriteAsync`
itself can be left intact for the metadata facets, or trivially re-expressed as Open + one
WriteRowGroup + dispose.

`CustomMetadata` is set on the writer before the first row group and is written into the single file
footer on dispose — it does **not** need to be repeated per row group. The existing facet KV
metadata (`spectrum_count`, `spectrum_array_index`, etc.) therefore still works unchanged; it is
computed once and attached before close. `[VERIFIED: Parquet.Net source ParquetWriter.cs;
CustomMetadata is a writer-level footer field]`

**Append-reopen also works** (EXP3): `ParquetWriter.CreateAsync(stream, append: true)` on a
re-opened file adds row groups (RowGroupCount went 1→2). This is the mechanism the existing
`ParquetSpectrumWriter.cs:104-108` uses via the high-level `ParquetSerializer` (`opts.Append=true`).
For this phase prefer the **single open writer** form (Decision 1 snippet) — fewer file reopens,
same result — but append-reopen is a proven fallback. `[VERIFIED: EXP3]`

### Decision 2 — Seekable target strategy (temp file → STORED zip): VALIDATED

The temp-file approach is correct **and necessary**. Confirmed (zip-entry test): a multi-row-group
parquet written to a temp `FileStream`, then bounded-`CopyTo`'d (64 KB buffer) into
`zip.CreateEntry(name, CompressionLevel.NoCompression).Open()`, produced an entry with
`CompressedLength == Length == 1560` (STORED) and read back with all 4 row groups intact via
`ParquetReader`. `[VERIFIED: /tmp/pqexp zip-entry test]`

Gotchas, all confirmed:
- **The zip entry stream is NOT seekable** (`CanSeek == False`, `CanWrite == True`). You can only
  forward-write into it. That is fine for `CopyTo` of bounded buffers, and is exactly why you cannot
  write Parquet *directly* into it (Decision 3). `[VERIFIED: printed CanSeek=False]`
- **STORED requires `CompressionLevel.NoCompression`** — already used by the current
  `AddStored` (`MzPeakSpectrumWriter.cs:631`). Keep it.
- **`leaveOpen: true`** on the `ZipArchive` ctor is already in use
  (`MzPeakSpectrumWriter.cs:266`) so the outer `Writer.BaseStream` survives for the final
  `Writer.Flush()/Close()`. Preserve this.
- **Constant memory:** the `CopyTo` reuses one bounded buffer; only ~64 KB is live during the copy
  regardless of facet size.

**Non-temp-file alternative — REJECTED.** Handing `ParquetWriter` a growable `MemoryStream` keeps the
whole facet in RAM (the v1 behavior we are removing). Handing it the non-seekable zip-entry stream
does **not** error and does **not** stream — it silently buffers the entire facet in an internal
`MemoryStream` and flushes on dispose (forward-only test: 0 seek calls, full data still landed,
447 bytes). So both non-temp routes reintroduce whole-facet memory. There is **no** streaming-direct-
to-zip option in Parquet.Net 5.0.1 because Parquet's footer-at-end format fundamentally needs seek.
**Recommendation: temp file is the approach.** `[VERIFIED: forward-only stream test + official source]`

### Decision 3 — Does `ParquetWriter` need a SEEKABLE stream? YES (and it fails silent if not)

The official `ParquetActor` base seeks (`SeekOrigin.Begin`/`End`, `GoBeforeFooterAsync`) — Parquet's
metadata footer sits at the end and references earlier offsets. `[CITED:
github.com/aloneguid/parquet-dotnet ParquetActor.cs]`

Critical empirical nuance for this codebase: passing a `CanSeek==false` stream does **not** raise.
Parquet.Net 5.0.1 detects non-seekability and wraps it in an internal buffer, writing everything on
dispose (forward-only test: `seekCalls=0`, output valid). **This is a trap** — it would look like
streaming works while actually buffering the whole facet. The plan must therefore assert the sink is a
real seekable `FileStream`, never the zip-entry stream. `[VERIFIED: forward-only stream test]`

### Decision 4 — Row-group flush cadence

**Trigger: row count.** Default cap **1,048,576 rows** to match the established
`ParquetSpectrumWriter.cs:41` (`ParquetSliceSize = 1_048_576`) — a known-good value already shipping
in this repo for the same point-row data. Recommend a single named const on `MzPeakSpectrumWriter`
(e.g. `RowGroupRowCap = 1_048_576`) so it is one-line tunable. A bytes-based secondary trigger is
**not needed** for point rows (each row is one (index, mz, intensity) ≈ 20 bytes, so 1M rows ≈ 20 MB
buffered per facet — small and predictable); leave it to Claude's discretion if a future facet has
wide rows. `[VERIFIED: ParquetSpectrumWriter.cs:41,104]`

**Spectra do NOT need to stay whole within a row group for point layout.** Each row is a single
point carrying its own `spectrum_index`; readers reconstruct a spectrum by selecting all rows with a
given `spectrum_index`, which is correct across row-group boundaries (EXP1 read all rows across 3
groups as one table). So a spectrum's points may freely span row groups. (This is the *opposite* of
`mzparquet`'s row-per-scan-array model where ParquetSpectrumWriter keeps a scan whole — that
constraint does not apply to mzPeak point layout.) Flush whenever the buffer hits the cap, mid-
spectrum is fine. `[VERIFIED: EXP1 cross-group read; point-layout schema MzPeakSpectrumWriter.cs:640]`

### Decision 5 — Per-scan robustness pattern + the real S4 root cause

**Mirror target — `MzMlSpectrumWriter.cs:422-433`:**
```csharp
SpectrumType spectrum = null;
int level;
try
{
    level = (int) _rawFile.GetScanEventForScanNumber(scanNumber).MSOrder; //applying MS level pre filter
    if (level <= ParseInput.MaxLevel)
        spectrum = ConstructMSSpectrum(scanNumber);
}
catch (Exception ex)
{
    Log.Error($"Scan #{scanNumber} cannot be processed because of the following exception: {ex.Message}");
    Log.Debug($"{ex.StackTrace}\n{ex.InnerException}");
    ParseInput.NewError();          // count, do NOT rethrow
}
// only on success: assign index, serialize, index++  (MzMlSpectrumWriter.cs:437-458)
```
`ParseInput.NewError()` (`ParseInput.cs:161-164`) just increments `_errors`; `NewWarn()`
(`ParseInput.cs:166-169`) increments `_warnings`. Errors are appropriate for a dropped scan (it is a
data loss), matching `MzMlSpectrumWriter`'s use of `NewError()` for the per-scan failure (it reserves
`NewWarn()` for recoverable metadata gaps, e.g. lines 785/1276/1429). Use **`NewError()`** for a
skipped scan.

**The current MzPeakSpectrumWriter ALREADY has a per-scan try/catch** at
`MzPeakSpectrumWriter.cs:142-232`, catching at line 227 with `Log.Error(...)` +
`ParseInput.NewError()`. So the gap is **not** a missing try/catch. The actual defect:

- Points are appended to the shared accumulators **early** — `dataIndex/dataMz/dataIntensity` at
  lines **154-159**, `peaksIndex/...` at lines **168-176** — using the current `ordinal`.
- The metadata `Record` is added at line **223** and `ordinal++` runs at line **225**, both at the
  *end* of the try body, after `GetScanStatsForScanNumber` (179), `ScanTrailer` (180),
  parent-derivation (184-190), and `BuildPrecursor` (217-221).
- If any step in 179-221 throws (the S4_5foldGHRP scan #25081 "Cannot get scan event" / stats class),
  the catch skips the record and does **not** advance `ordinal` — **but the data/peak points already
  pushed at 154-176 remain under that ordinal**. The next good scan reuses the same `ordinal`,
  producing: (a) orphan `spectra_data` points whose `spectrum_index` has no metadata row, and (b) two
  spectra collapsed onto one ordinal. Facet parity (`spectrum_count` == distinct `spectrum_index`)
  breaks. In the streaming design this is worse: orphan points may already have been flushed to a
  row group and cannot be retracted.

**Required skip semantics (all-or-nothing per scan):**
1. Read + build everything for the scan into **per-scan local staging** (a local point buffer for
   data, one for peaks, a local `Record`) *before* touching any shared accumulator or streaming
   writer.
2. Wrap the entire read+build in the try. On exception → `Log.Error` + `ParseInput.NewError()` +
   `continue`; nothing was staged into shared state, so **no ordinal is consumed and no facet got
   rows**.
3. Only after the scan fully succeeds: assign the current `ordinal` to the staged rows, append them to
   the row-group buffers (flushing if at cap), add the `Record`, then `ordinal++`.
This guarantees dense ordinals and data/metadata/peaks parity even when scans are skipped, and it
composes with row-group flushing because a partially-built scan never reaches a buffer.

### Decision 6 — Memory measurement + big-file test

**Measure peak RSS on macOS with `/usr/bin/time -l`** (confirmed: prints both
`maximum resident set size` and `peak memory footprint` in bytes):
```bash
/usr/bin/time -l dotnet ThermoRawFileParser/bin/x64/Release/net8.0/ThermoRawFileParser.dll \
  -i <big>.raw -b /tmp/big.mzpeak -f mzpeak 2>&1 | grep -E "maximum resident set size|peak memory footprint"
```
Runs **natively on arm64** — no Rosetta/`arch -x86_64` needed (RawFileReader 8.0.37 is AnyCPU; see
`RUNNING.md`). Use `DOTNET_ROLL_FORWARD=LatestMajor DOTNET_ROLL_FORWARD_TO_PRERELEASE=1` if a net8
runtime is absent. `[VERIFIED: /usr/bin/time -l on this machine; RUNNING.md native-arm64 note]`

**Concrete LARGE corpus files** under `~/Claude/mzML2mzPeak/data` (verified present, sorted by size):
| File | Size | Use |
|------|------|-----|
| `sdrf-examples/PXD009909/raw/70JG_02.raw` | 954 MB | Primary MEM-02 Orbitrap (~1 GB) |
| `sdrf-examples/PXD009909/raw/70JG_03.raw` | 919 MB | Alt MEM-02 |
| `sdrf-examples/PXD011799/raw/...fr3_MS2.raw` | 728 MB | Mid-size sanity |
| `raw-examples/thermo-astral-MSV000100943/20240912_WFB_exp01_magnet_5_0.raw` | **8.4 GB** | Stretch / OOM-killer case |
`[VERIFIED: ls -lh of the corpus]`

**Bad-scan ROB file:** `raw-replacements/bruker-impact-sub__PXD076459/S4_5foldGHRP.raw` (418 MB) —
fails on scan #25081 per CONTEXT.md. `[VERIFIED: file present, 418 MB]`

**`mzpeak-validate` on large archives:** the tool is `~/anaconda3/bin/mzpeak-validate` and supports
`--quick` (skip full-column data scans, metadata-only) for fast checks on huge archives, plus
`--json`/`--log` for reports. For the 8.4 GB Astral output use `--quick` first to confirm structure,
then a full pass if time permits. For the ~1 GB Orbitrap, a full validate is the conformance gate
(must be 0/0). `[VERIFIED: mzpeak-validate --help]`

## Current Writer Buffering — exact map (Question 3)

All five facets are built **fully in memory** then handed to `WriteFacet` →
`MzPeakParquet.WriteAsync` (single row group) → `byte[]`, then `AddStored` copies the bytes into the
zip. Per-facet detail in `MzPeakSpectrumWriter.cs`:

| Facet | Accumulators (declared) | Filled in loop | Built (single row group) | Bounded by |
|-------|-------------------------|----------------|--------------------------|------------|
| `spectra_data` | `dataIndex/dataMz/dataIntensity` (126-128) | 154-159 | `BuildPointFacet` (249) → `WriteFacet` (638-661) | **point count (UNBOUNDED)** |
| `spectra_peaks` | `peaksIndex/peaksMz/peaksIntensity` + `peakSpectra` (130-133) | 168-176 | `BuildPointFacet` (251) | **peak-point count (UNBOUNDED)** |
| `chromatograms_data` | `chromTime/chromIntensity/chromMsLevel` (243-245) | filled post-loop by `CaptureTic` (246, 323-388) | `BuildChromatogramDataFacet` (257) | scan count (bounded; one TIC point per scan) |
| `spectra_metadata` | `records` list of `Record` (135) | 192-224 | `BuildMetadataFacet` (260, 663-767) | spectrum count (bounded) |
| `chromatograms_metadata` | none (single TIC row) | n/a | `BuildChromatogramMetadataFacet` (258) | constant (1 row) |

**Per-scan loop:** `for (scanNumber ...)` at 140; the scan body 142-233 with try/catch.
**Ordinals/counts/metadata assembled:** `ordinal` declared 138, used as `spectrum_index` for every
appended point (156/170) and as `Record.Ordinal` (194); `ordinal++` at 225. Per-spectrum
`DataPointCount` set at 207 from `mz.Length`; `PeakCount` at 175/208; `BasePeakMz/BasePeakIntensity`
from `mzData` at 204-205; `TotalIonCurrent` from `scanStats.TIC` at 206.
**Co-resident packed metadata build:** `AddMetadataBlocks` (1119-1152) assembles the file-level JSON
blocks AFTER all CV-named columns/params are emitted (so `cv_list` is exhaustive), writing them both
to the parquet footer KV (`custom[...]`) and stashing `_metadataBlocks` for `BuildIndex` (1373-1382).

**Precise seams to convert (DATA facets → streaming, metadata buffered):**
1. **Replace the three growing `List<>` data accumulators** (126-133 for data/peaks; 243-245 for
   chrom-data) with **row-group-sized staging buffers** plus three streaming handles (Decision 1)
   over three temp `FileStream`s. The chromatograms_data facet is bounded by scan count and could be
   left buffered like the metadata facets — but CONTEXT lists it as a streamed facet; either is
   defensible (one TIC point per scan ≈ trivial). Recommend streaming it too for uniformity, or note
   the buffered choice in SUMMARY.
2. **At the per-scan append points** (154-176): write into the *staging* buffers; when a buffer hits
   `RowGroupRowCap`, call `WriteRowGroupAsync` (Decision 1) using the **same** `Column()`/`BuildPoint`
   leaf logic on the buffered slice, then clear the buffer.
3. **Keep `records` (metadata) buffered** exactly as today — `BuildMetadataFacet`,
   `BuildChromatogramMetadataFacet`, `AddMetadataBlocks`, `BuildIndex` are untouched.
4. **At finalize** (current 263-281): flush each streaming handle's residual buffer as a final row
   group, dispose each `ParquetWriter` (footer to temp file), then open the `ZipArchive` and for each
   facet `AddStoredFromFile(zip, name, tempPath)` (bounded `CopyTo`, Decision 2) and delete the temp.
   The metadata/index/chrom-metadata facets still go through the existing `AddStored(byte[])`.

**Single-pass viability for per-spectrum counts/base-peak/TIC:** YES — no second pass needed. Every
metadata value (`DataPointCount` from `mz.Length`, `PeakCount` from the peak array length,
`BasePeakMz/Intensity` from `mzData`, `TotalIonCurrent` from `scanStats`) is computed **per scan
inside the loop** and stored on the `Record` *before* any flush. Because metadata stays fully
buffered (`records`), all of it is available at finalize regardless of how many data row groups were
already flushed. The streaming of the *data* facets does not remove any information the metadata
needs — they are independent. The only requirement is the all-or-nothing staging (Decision 5) so a
skipped scan contributes neither a `Record` nor data rows. No running re-accumulation, no reread.

## Streaming API Snippet (consolidated, for the planner)

```csharp
// --- MzPeakParquet (additive; existing WriteAsync untouched) ---
public static async Task<ParquetWriter> OpenAsync(Stream seekableSink, ParquetSchema schema,
    IReadOnlyDictionary<string,string> customMetadata) { /* CreateAsync + Zstd + CustomMetadata */ }

public static async Task WriteRowGroupAsync(ParquetWriter w, ParquetSchema schema,
    IDictionary<DataField,(Array,int[],int[])> cols) { /* one CreateRowGroup + per-leaf WriteColumnAsync */ }
// dispose the ParquetWriter to flush the footer.

// --- MzPeakSpectrumWriter finalize: temp file -> STORED zip (constant memory) ---
private static void AddStoredFromFile(ZipArchive zip, string name, string tempPath)
{
    var entry = zip.CreateEntry(name, CompressionLevel.NoCompression);   // STORED
    using (var es = entry.Open())                                        // non-seekable, write-only
    using (var src = File.OpenRead(tempPath))
        src.CopyTo(es, 1 << 16);                                          // 64 KB bounded buffer
}
```

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Multi-row-group Parquet | Manual offset/footer math | One `ParquetWriter` + N `CreateRowGroup()` (Decision 1) | Footer/stat bookkeeping is exactly what Parquet.Net does; proven in EXP1/EXP3 |
| Streaming bytes into the zip | Read whole temp file into a `byte[]` then `AddStored` | `Stream.CopyTo(entryStream, 64KB)` (Decision 2) | Whole-`byte[]` reintroduces the memory blowup we are removing |
| Seekable target | A custom buffering stream | A temp `FileStream` | Parquet.Net already buffers non-seekable streams in RAM — defeats the goal silently |
| Per-scan isolation | New retry/exception framework | The `MzMlSpectrumWriter` try/`NewError`/skip pattern | Established in-repo, consistent error counting |

**Key insight:** the only genuinely new code is ~2 thin `MzPeakParquet` methods and a temp-file
lifecycle in `Write`; everything else is *reusing* existing column/leaf/`AddStored` logic on smaller
slices.

## Common Pitfalls

### Pitfall 1: Non-seekable sink silently buffers (no error)
**What goes wrong:** Handing `ParquetWriter` the zip-entry stream "works" and produces a valid file,
so it looks done — but memory is unbounded again.
**Why:** Parquet.Net 5.0.1 wraps `CanSeek==false` streams in an internal `MemoryStream`.
**How to avoid:** Always write Parquet to a temp `FileStream`; assert `sink.CanSeek` in the handle
opener.
**Warning signs:** RSS scales with file size despite "streaming"; zero temp files on disk.

### Pitfall 2: Early data append before scan finalize (the S4 bug)
**What goes wrong:** Orphan points under an ordinal whose metadata never materialized; ordinal reuse.
**Why:** Current loop appends at 154-176 but finalizes/`ordinal++` at 223-225.
**How to avoid:** Stage all of a scan's rows locally; commit (append + ordinal++) only on full
success (Decision 5).
**Warning signs:** `mzpeak-validate` parity error; distinct `spectrum_index` ≠ metadata row count.

### Pitfall 3: Splitting a spectrum across row groups assumed unsafe
**What goes wrong:** Over-engineering a "keep scan whole" flush like `ParquetSpectrumWriter`.
**Why:** That constraint is real for `mzparquet` (row = scan array) but NOT for mzPeak point layout
(row = one point with its own `spectrum_index`).
**How to avoid:** Flush at the row cap mid-spectrum; readers select by `spectrum_index` across groups
(EXP1).

### Pitfall 4: Temp files leaked on exception
**What goes wrong:** A mid-conversion throw leaves multi-GB temp files behind.
**How to avoid:** `try/finally` around the streaming handles + temp paths; delete temps in `finally`
(mirror the existing `finally { Writer.Close(); }` at 278-281).

## Environment Availability

| Dependency | Required By | Available | Version | Fallback |
|------------|------------|-----------|---------|----------|
| .NET SDK | build/run/test | ✓ | 10.0.301 (rolls forward from net8 target) | — |
| Parquet.Net | streaming writer | ✓ | 5.0.1 (`~/.nuget/.../parquet.net/5.0.1`) | — |
| ThermoFisher RawFileReader | RAW read | ✓ | 8.0.37 (AnyCPU, lib/net8.0) | — |
| `mzpeak-validate` | conformance gate | ✓ | `~/anaconda3/bin/mzpeak-validate` (`--quick`/`--json`/`--log`) | — |
| `/usr/bin/time -l` | MEM-02 RSS | ✓ | macOS builtin (max RSS + peak footprint) | — |
| x64 .NET 8 runtime | MGF tests only | ✓ | `~/.dotnet-x64` | MGF tests self-`Ignore` on arm64 |
| Large corpus RAW | MEM-02 | ✓ | 954 MB Orbitrap; 8.4 GB Astral | — |
| Bad-scan RAW | ROB | ✓ | S4_5foldGHRP.raw 418 MB | — |

**Missing dependencies:** none — every gate dependency is present.

## Validation Architecture

### Test Framework
| Property | Value |
|----------|-------|
| Framework | NUnit (`ThermoRawFileParserTest`, net8.0) — 52 tests currently |
| Config file | `ThermoRawFileParser/ThermoRawFileParserTest/*.csproj` |
| Quick run command | `DOTNET_ROLL_FORWARD=LatestMajor dotnet test ThermoRawFileParser/ThermoRawFileParser.sln --filter MzPeakWriterTests` |
| Full suite command | `DOTNET_ROLL_FORWARD=LatestMajor dotnet test ThermoRawFileParser/ThermoRawFileParser.sln` (native arm64) |

### Phase Requirements → Test Map
| Req ID | Behavior | Test Type | Automated Command | File Exists? |
|--------|----------|-----------|-------------------|-------------|
| MEM-01/03 | Multi-row-group output reads back identically to v1 | unit | `dotnet test --filter MzPeakWriter...MultiRowGroup` | ❌ Wave 0 (new test) |
| MEM-02 | Large RAW converts under bounded RSS + validates 0/0 | manual/measured | `/usr/bin/time -l dotnet ... -i 70JG_02.raw`; then `mzpeak-validate` | ❌ Wave 0 (script/manual) |
| ROB-01/02 | Bad-scan file completes, skip logged, parity holds | integration | convert `S4_5foldGHRP.raw`; assert skipped scan absent, distinct `spectrum_index` == metadata rows | ❌ Wave 0 (new test/script) |
| no-regress | `small.RAW` byte-semantically identical, validator 0/0, 52/52 | unit+validate | full suite + `mzpeak-validate small.mzpeak` | ✅ existing harness |

### Sampling Rate
- **Per task commit:** `dotnet test --filter MzPeakWriterTests`
- **Per wave merge:** full `dotnet test` (52/52) + `mzpeak-validate` on `small.mzpeak`
- **Phase gate:** full suite green + large-file MEM-02 + ROB file before `/gsd:verify-work`

### Wave 0 Gaps
- [ ] New unit test: multi-row-group `spectra_data` reads back identical to single-group v1 (drive
      `MzPeakParquet` streaming path; assert RowGroupCount > 1 and identical (index,mz,intensity)
      multiset).
- [ ] New robustness test/script: convert a file with a forced/known bad scan; assert conversion
      exits 0, error count incremented, skipped ordinal absent, parity intact.
- [ ] MEM-02 measurement script wrapping `/usr/bin/time -l` around a ~1 GB conversion (assert peak
      RSS under an agreed budget — budget value is Claude's discretion / SUMMARY-documented).

## State of the Art

| Old Approach (v1) | Current Approach (v2 this phase) | Why |
|-------------------|----------------------------------|-----|
| Full in-memory point accumulation, single row group per facet (`02-01-SUMMARY` lines 181-186) | Bounded row-group streaming to temp files, then STORED copy | Constant memory on multi-GB RAW |
| x64-pinned, Rosetta run (`01-01-SUMMARY` lines 74-82) | Native arm64, AnyCPU, RawFileReader 8.0.37 | RawFileReader 8.0.37 is AnyCPU (no native dylib) |
| Whole scan kept in one row group (`ParquetSpectrumWriter`) | Points may span row groups (point layout) | mzPeak row = one point; reader joins on `spectrum_index` |

## Assumptions Log

| # | Claim | Section | Risk if Wrong |
|---|-------|---------|---------------|
| A1 | S4_5foldGHRP #25081 fails *after* the data-append (in stats/precursor build), making early append the parity-break vector | Decision 5 | If it instead fails at `ReadMZData` (before append, line 149), the existing catch already skips cleanly and only the streaming refactor's all-or-nothing staging is needed defensively — still correct, just over-cautious. Low risk; the fix is safe either way. |
| A2 | 1,048,576-row cap yields acceptable peak RSS for the data facets | Decision 4 | If buffers prove too large with three open facets, lower the cap; trivially tunable. |
| A3 | Streaming chromatograms_data is worth it (vs leaving it buffered) | Decision 4 / seam 1 | It is bounded by scan count anyway; buffering it is equally valid. Document the choice in SUMMARY. |

## Open Questions

1. **Exact failure point of S4_5foldGHRP #25081**
   - What we know: CONTEXT says it "fails on scan #25081"; the loop already catches and counts.
   - What's unclear: whether it throws before or after the data-append (line 154).
   - Recommendation: the all-or-nothing staging (Decision 5) is correct regardless; optionally log
     the stack on first occurrence during implementation to confirm A1.

2. **8.4 GB Astral full validate feasibility**
   - What we know: `--quick` exists for metadata-only validation.
   - What's unclear: wall-clock of a full-column validate on an 8 GB-derived archive.
   - Recommendation: `--quick` for the stretch case; full validate is the gate only for the ~1 GB
     Orbitrap.

## Sources

### Primary (HIGH confidence)
- Empirical experiments on Parquet.Net 5.0.1 (this machine, `/tmp/pqexp`): EXP1 multi-row-group
  write+read (RowGroupCount=3, 9 rows; pyarrow cross-check), EXP3 append-reopen (RowGroupCount=2),
  forward-only stream test (silent buffering, 0 seeks), temp-file→STORED-zip test
  (CompressedLength==Length, RowGroupCount=4 read back).
- `/usr/bin/time -l` output on this machine (max RSS + peak footprint).
- Repo source: `MzPeakSpectrumWriter.cs`, `MzPeakParquet.cs`, `MzMlSpectrumWriter.cs`,
  `ParquetSpectrumWriter.cs`, `ParseInput.cs`, `SpectrumWriter.cs`, `RUNNING.md`,
  v1 `01-01`/`02-01` SUMMARY files, CONTEXT.md.

### Secondary (MEDIUM confidence)
- [github.com/aloneguid/parquet-dotnet ParquetActor.cs](https://github.com/aloneguid/parquet-dotnet/blob/master/src/Parquet/ParquetActor.cs) — seek-on-footer requirement.
- [github.com/aloneguid/parquet-dotnet ParquetWriter.cs](https://github.com/aloneguid/parquet-dotnet/blob/master/src/Parquet/ParquetWriter.cs) — append mode, CustomMetadata.

## Metadata

**Confidence breakdown:**
- Streaming API: HIGH — ran the exact API on the installed 5.0.1.
- Seekable requirement: HIGH — official source + empirical silent-buffer test.
- Temp-file→zip: HIGH — full round-trip test (STORED + readable).
- Robustness root cause: MEDIUM-HIGH — code path is unambiguous; exact S4 throw line is A1.
- Memory measurement / corpus: HIGH — tool + files verified present.

**Research date:** 2026-06-14
**Valid until:** 2026-07-14 (stable; Parquet.Net 5.0.1 pinned in csproj)
