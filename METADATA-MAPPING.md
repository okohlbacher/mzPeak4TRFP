# Handoff: Thermo RAW → mzPeak metadata mapping

How ThermoRawFileParser (TRFP) maps Thermo RAW metadata into the HUPO-PSI **mzPeak**
format. Covers both layers of mapping — the **standard CV layer** (handled by the vendored
mzPeak.NET library) and the **verbatim vendor layer** (TRFP-owned proprietary facets) — plus
the per-scan flow, the on-disk facet schemas, the CLI surface, and the deliberate design calls.

Audience: a developer picking up the mzPeak writer. Read alongside the source files cited below.

---

## 1. Big picture

There are **two metadata mappings**, by design:

| Layer | What it captures | Who owns it | Where it lands |
|---|---|---|---|
| **Standard / CV** | Everything that has a PSI-MS controlled-vocabulary term (MS level, polarity, base peak, TIC, scan windows, precursor/isolation/activation, instrument config, software, sample, TIC chromatogram) | **Vendored mzPeak.NET** (`MZPeak.Thermo.ThermoMZPeakWriter` + `ConversionContextHelper`) | The standard mzPeak facets: `spectra_metadata`, `chromatograms_metadata`, and the run-level metadata block in `mzpeak_index.json` |
| **Verbatim / vendor** | Everything Thermo exposes that mzML's CV **cannot** represent — the per-scan Trailer Extra bag, tune data, run header, instrument methods, status log, error log | **TRFP** (`Writer/MzPeak/Vendor*.cs`) | Proprietary non-CV Parquet entries injected into the same archive (`vendor_*.parquet`), opt-in via `--vendor-metadata` |

The guiding principle: **never lose vendor information**. mzML forces everything through CV
terms and drops what doesn't fit; mzPeak lets us keep the standard mapping *and* attach the raw
Thermo metadata verbatim so nothing is lost in translation.

TRFP delegates **all** spectrum/chromatogram/standard-metadata writing to mzPeak.NET. The only
TRFP-authored mapping code is the vendor layer and the per-scan orchestration that drives the
library.

---

## 2. Architecture & control flow

Entry point: `ThermoRawFileParser/Writer/MzPeakSpectrumWriter.cs` → `Write(raw, first, last)`.

```
MzPeakSpectrumWriter.Write
  ├─ ConfigureWriter(".mzpeak")                 # opens the output FileStream
  ├─ new ZipStreamArchiveWriter(Writer.BaseStream)
  ├─ new ThermoMZPeakWriter(storage, useChunked, peakArrayIndex)   # mzPeak.NET
  ├─ thermoWriter.InitializeHelper(raw)         # builds run-level metadata + scan-type maps
  │
  ├─ for each scan in [first..last]:
  │     read filter → MS level (guarded; TryGetValue)
  │     MS-level filter (--msLevel) — cheap skip before heavy reads
  │     READ PHASE (guarded): ScanStatistics, SegmentedScan, CentroidStream,
  │                           ExtractPrecursorAndTrailerMetadata
  │     COMMIT PHASE:
  │        AddSpectrumData      → spectra_data   (profile or centroid points)
  │        AddSpectrumPeakData  → spectra_peaks  (centroids, if a profile scan had them)
  │        AddSpectrum/AddScan/AddPrecursor/AddSelectedIon → spectra_metadata
  │        vendorTrailers.Append(...) (tall) — streamed during the loop
  │
  ├─ TIC chromatogram → chromatograms_data / chromatograms_metadata
  ├─ if --vendor-metadata: WriteVendorFacets(...)   # the 6 vendor_*.parquet entries
  ├─ thermoWriter.Close()                       # writes spectra/chromatogram facets + mzpeak_index.json
  ├─ Writer.Flush(); committed = true
  └─ if --vendor-metadata-json: WriteVendorJsonSidecar(...)   # after commit, self-guarded
```

Key library types (in `mzpeak.net/MZPeakNet.Thermo/Writer.cs`):
- **`ThermoMZPeakWriter`** — thin Thermo-flavoured wrapper over `MZPeakWriter`. Exposes
  `AddSpectrumData`, `AddSpectrumPeakData`, `AddSpectrum`, `AddScan`, `AddPrecursor`,
  `AddSelectedIon`, `AddChromatogram*`, and the proprietary-entry hooks (see §5).
- **`ConversionContextHelper`** — the actual Thermo→CV translator. `Initialize(raw)` builds the
  trailer-label index, the per-MS-level counts, and the "previous MS level" cache used to resolve
  precursors. Holds all the `Extract*`/`Get*` methods that read Thermo APIs and emit `Param`s.

---

## 3. Standard CV mapping (mzPeak.NET)

This layer is upstream code; TRFP only calls it. Summary of what each call emits (exact CURIEs in
`mzpeak.net/MZPeakNet.Thermo/Writer.cs`):

**Per spectrum row (`spectra_metadata` → `spectrum` struct), via `AddSpectrum`:**
- `MS:1000511` ms level, `MS:1000465` scan polarity (from the scan filter)
- `MS:1000525` spectrum representation → child `MS:1000127` centroid / `MS:1000128` profile
- `MS:1000559` spectrum type → emitted as the concrete child `MS:1000294` *mass spectrum*
  (the validator checks inflected column names with `use_term=false`; see §7)
- `MS:1000504` base peak m/z, `MS:1000505` base peak intensity, `MS:1000285` TIC (from `ScanStatistics`)
- `MS:1003060` number of data points, `MS:1003059` number of peaks — **see §7 for the coalescing rule**

**Per scan (`scan` struct), via `AddScan`:** scan start time, filter string, ion injection time,
mass resolution, scan window lower/upper limit; FAIMS compensation voltage when the filter has one.

**Per precursor (`precursor` / `selected_ion` structs), via `AddPrecursor`/`AddSelectedIon`:**
isolation window target + lower/upper offset, activation (dissociation method + collision energy),
charge state, selected-ion m/z. The precursor's master scan is resolved from the Trailer "Master
Scan" value, falling back to the most-recent lower-MS-level scan (`ConversionContextHelper`
`PreviousMSLevels` cache).

**Run-level metadata block** (`mzpeak_index.json`), via `InitializeHelper`: `FileDescription`
(source file with Thermo nativeID format; `MS:1000524` data file content + `MS:1000579`/`MS:1000580`
MS1/MSn children), instrument configuration, software (Xcalibur + this converter), sample, data
processing method.

**Chromatogram:** the run TIC trace (`TraceType.TIC`) → `chromatograms_data` + `chromatograms_metadata`.

---

## 4. Verbatim vendor mapping (TRFP) — opt-in `--vendor-metadata[=tall|wide|both]`

Six proprietary, non-CV Parquet facets are injected into the archive. Code: `Writer/MzPeak/`.
All vendor reads are **best-effort**: a failing Thermo API logs a `Warn` and degrades that facet,
never aborting the (often multi-GB) conversion.

| Facet (entry) | Layout | Schema (columns) | Source | Built by |
|---|---|---|---|---|
| `vendor_scan_trailers.parquet` | **tall** (1 row per scan×label) | `ordinal:u64, scan_number:i32, label, value, value_float` | per-scan Trailer Extra bag | `VendorTrailerFacetStream` (streamed during the scan loop, bounded row groups for constant memory) |
| `vendor_scan_trailers_wide.parquet` | **wide** (1 row per scan) | `ordinal, scan_number, + one TYPED column per trailer label` (numeric→double, else string) | same, pivoted | `VendorWideTrailerFacet` |
| `vendor_trailer_schema.parquet` | — | `ordinal, label, data_type, column_name, value_kind` | trailer header | `VendorMetadataFacets.WriteVendorTrailerSchema` |
| `vendor_file_metadata.parquet` | — | `category, entry_index, label, value, value_float` | instrument data, sample info, run header, tune data, status-log header, instrument methods | `WriteVendorFileMetadata` |
| `vendor_status_log.parquet` | — | `position, rt, label, value, value_float` | full status log (all time points) | `WriteVendorStatusLog` |
| `vendor_error_log.parquet` | — | `index, rt, message` | run error log (reflection-read) | `WriteVendorErrorLog` |

**Keying:** every per-scan vendor row carries both the dense `ordinal` (0..N-1, joins the `spectra_*`
facets) and the verbatim Thermo `scan_number`. `ordinal` is the join key to standard data; `scan_number`
preserves the original identity.

**Typed values:** `value` is always the exact verbatim source string; `value_float` is the *typed*
numeric value when the trailer/log datatype is numeric (avoids culture-dependent re-parsing). For the
wide layout, numeric labels become nullable `double` columns, everything else stays string.

**Tall vs wide:** `tall` is the canonical, schema-stable layout (good for arbitrary trailers).
`wide` is a convenience pivot (one column per label) for analysts; `both` emits both. The exact
label→column-name mapping (sanitized, de-duplicated) is recorded in `vendor_trailer_schema`.

**`--vendor-metadata-json`** writes an additional human-readable `*.vendor.json` sidecar (instrument,
sample, run header, tune, status-log header, instrument methods, trailer schema). It is written
*after* the archive is committed and is self-guarded, so a sidecar failure never harms the archive.

---

## 5. The injection seam (how vendor facets get into the archive)

mzPeak.NET's `MZPeakWriter` is private inside `ThermoMZPeakWriter`. Two public hooks were added
upstream-side (in the vendored copy) so TRFP can write extra entries without reaching into internals:

```csharp
Stream                          StartProprietaryEntry(FileIndexEntry)          // raw bytes (copy a temp parquet in)
ParquetSharp.IO.ManagedOutputStream StartProprietaryParquetEntry(FileIndexEntry)  // write parquet directly
```

Both flush the standard CV content first and flip the writer to its "other data" state, so
proprietary entries are appended after the standard facets. `EntityType(Other, "proprietary")` +
`DataKind.Proprietary` mark them as non-CV. Shared Parquet setup (zstd + embedded schema) lives in
`VendorArrow.OpenWriter`.

---

## 6. CLI surface

| Flag | Effect |
|---|---|
| `-f 4` / `--format=4` | Output mzPeak |
| `--point` | Emit `spectra_data` in point layout (v1) instead of the default chunk layout |
| `--vendor-metadata[=tall\|wide\|both]` | Emit the vendor facets (default `tall`) |
| `--vendor-metadata-json[=path]` | Also write the readable JSON sidecar |
| `-L=`/`--msLevel=` | Restrict to MS levels (e.g. `-L=1`) |

m/z is stored as lossless `Float64` in both layouts (no Numpress). The old bespoke
`--lossless`/`--no-numpress`/`--chunk-size` flags were removed — they were no-ops after the
mzPeak.NET refactor and the lossy-default warning was false.

---

## 7. Non-obvious decisions & quirks (read before changing anything)

- **`number_of_data_points` vs `number_of_peaks`.** Whatever `AddSpectrumData` writes lands in
  `spectra_data`, so its row count is always `number_of_data_points` — *regardless of profile vs
  centroid*. mzPeak.NET reports that count as `PeakCount` for centroid scans (`isProfile=false`), so
  TRFP coalesces `DataPointCount ?? PeakCount` into `number_of_data_points`, and sets
  `number_of_peaks` only from the separate `spectra_peaks` facet. Without this, the point-layout
  validator (`per_spectrum_data_points`) fails; the chunk layout masks it. See
  `MzPeakSpectrumWriter.cs` commit phase.
- **Best-effort vendor reads.** Vendor metadata is opt-in and Thermo's APIs are flaky; per-facet
  failures `Log.Warn` and degrade, they do **not** abort. Per-scan typed-value failures stay silent
  (the verbatim `value` string is still captured; only `value_float` degrades).
- **Wide-column ordinal join.** Wide-trailer values are joined to columns by position — safe because
  Thermo's per-file trailer schema is fixed and `GetTrailerExtraValues` returns values in header order.
- **`CHAR` trailers are strings,** not numeric (a character coerced to double is meaningless).
- **Delete-on-failure.** Any failure before commit deletes the partial `.mzpeak` (no corrupt output);
  `committed` is set only after the final flush.
- **Read-then-commit phasing.** Each scan's data is fully read (in a guarded block that skips+counts
  bad scans, matching TRFP's mzML writer) before any write, so a bad scan can't half-write a spectrum.

---

## 8. Validation & test status

- Conformance tool: `mzpeak-validate` (profile `mzpeak-0.9`). TRFP output validates **0 errors /
  0 warnings** across CLI modes, five Thermo instrument classes (ion trap, Orbitrap Velos, FT-ICR,
  Fusion Lumos, Orbitrap Astral), and large files (21 GB / 744k spectra).
- The standard mapping matches the official `MZPeakNet.AppTest` reference converter's validation
  profile exactly (proving residual issues, when any, are upstream-library, not TRFP).
- Round-trip: TRFP mzML vs mzPeak agree exactly on spectrum count, MS-level distribution, and TIC;
  the actual m/z arrays are bit-exact (lossless) for all non-zero signal.
- Tests: `MzPeakVendorMetadataTests`, `MzPeakDifferentialTests` (semantic equivalence vs reference),
  full suite 27/27.
- Six upstream mzPeak.NET conformance bugs were patched in the vendored copy (footer counts,
  unsigned count columns, two required CV terms, chunk peak-count, swapped units) — see commit
  `9153a8d` and the draft PR `HUPO-PSI/mzPeak.NET#1`.

---

## 9. Where to look

| Concern | File |
|---|---|
| Scan-loop orchestration, data-point/peak coalescing, delete-on-failure | `ThermoRawFileParser/Writer/MzPeakSpectrumWriter.cs` |
| Vendor facet orchestration, file-metadata/status/error logs, JSON sidecar, `VendorArrow` | `ThermoRawFileParser/Writer/MzPeak/VendorMetadataFacets.cs` |
| Tall scan-trailer streaming | `ThermoRawFileParser/Writer/MzPeak/VendorTrailerFacetStream.cs` |
| Wide scan-trailer pivot + label classification | `ThermoRawFileParser/Writer/MzPeak/VendorWideTrailerFacet.cs` |
| CLI flag wiring | `ThermoRawFileParser/MainClass.cs` |
| Standard CV mapping (Thermo→PSI-MS), precursor resolution, run metadata | `mzpeak.net/MZPeakNet.Thermo/Writer.cs` |
| Reference converter (for diffing) | `mzpeak.net/MZPeakNet.AppTest/Program.cs` |
