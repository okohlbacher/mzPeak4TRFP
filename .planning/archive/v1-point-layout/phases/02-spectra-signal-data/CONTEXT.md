# Phase 2 Context: Spectra Signal Data

**Requirements:** DATA-01, DATA-02, DATA-03, DATA-04
**Depends on:** Phase 1 (the `MzPeakParquet` helper + writer skeleton + STORED-zip pipeline).

## Intent

Replace Phase 1's single-spectrum placeholder with the full point-layout signal for ALL in-range
spectra, plus a centroid `spectra_peaks` facet, with canonical widths and ascending m/z. Metadata
fidelity stays minimal (Phase 3); this phase is about the signal facets being complete and correct.

## Decisions (locked for this phase)

- **`spectra_data.parquet`** = the primary signal arrays for every in-range spectrum, point layout
  `point: struct<spectrum_index:u64, mz:f64, intensity:f32>`, one row per data point, contiguous per
  spectrum. `spectrum_index` is the 0-based output ordinal (dense), not the Thermo scan number.
- **DUAL representation (match the reference archive `refs/mzPeak/small.unpacked.mzpeak`, leverage Thermo label peaks):**
  - `spectra_data` ← the as-acquired arrays for EVERY in-range scan via `ReadMZData(centroid=false)`
    (profile for profile scans; the centroid SegmentedScan for centroid-only scans).
  - `spectra_peaks` ← the Thermo `CentroidStream` (label peaks) via `ReadMZData(centroid=true)` ONLY when
    `scanEvent.ScanData == ScanDataType.Profile && scan.HasCentroidStream` (a profile scan that ALSO carries
    label peaks). Centroid-only scans go to `spectra_data` only — never duplicate, never invent data.
  - Use the exact availability checks the existing writers use (`HasCentroidStream`, `scanEvent.ScanData`).
  - VER-02 facet-equivalence vs mzML2mzPeak is a Phase-4 concern (the differential will run TRFP's mzML
    writer in profile mode to align with `spectra_data`); do NOT claim facet equivalence in this phase.
- **m/z sorted ascending per spectrum**, multiset preserved (stable sort of (mz,intensity) pairs; never
  merge or drop duplicate-m/z points). mz→f64, intensity→f32 (uniform narrowing).
- **`sorting_rank: 0`** must be declared for `point.mz` in the `spectrum_array_index` JSON (resolves the
  Phase-1 validator INFO `mz_monotonic_data`; since we sort, declare sorted).
- **Filters:** honor existing `ParseInput` MS-level (`MsLevel`/`MaxLevel`) and scan-range filters — a
  filtered-out scan produces zero rows in every facet and is not counted.
- **Counts:** `spectrum_count` and per-facet point counts in Parquet CustomMetadata must reflect the
  real totals across all written spectra.
- **`mzpeak_index.json`** lists `spectra_peaks` only if it is actually written (never list an absent facet).

## Constraints / runtime (from Phase 1)

- Build: `DOTNET_ROLL_FORWARD=LatestMajor DOTNET_ROLL_FORWARD_TO_PRERELEASE=1 dotnet build -c Release`.
- Run/test on arm64: the build is x64-pinned (Thermo CommonCore) → run via the Rosetta x64 .NET 8
  runtime at `~/.dotnet-x64` (`arch -x86_64 ~/.dotnet-x64/dotnet …`; tests need `DOTNET_ROOT_X64=$HOME/.dotnet-x64`).
- Reuse the `MzPeakParquet` helper (Column/WriteAsync/BuildParamField/CvColumn) — do not duplicate it.
- m/z=f64, intensity=f32; STORED zip; ZSTD-internal Parquet.
- Compact code; explicit usings; BOM-free new files; NO comments referencing harness/process/phases.
- Memory: building each facet fully in a MemoryStream is acceptable for the prototype, but consider
  Parquet row-group batching (the existing ParquetSpectrumWriter flushes at ~1M rows) so a large RAW
  does not blow memory. Note the chosen approach in the SUMMARY.

## Verification (this phase)

- Build green; full NUnit suite green (add Phase-2 unit tests for sort/multiset preservation + width coercion).
- Convert `small.RAW`; `mzpeak-validate` shows the `mz_monotonic_data` INFO resolved (sorting_rank=0) and
  no NEW errors beyond the still-expected Phase-3 `scan`-facet error.
- Differential vs the reference: spectrum count and per-spectrum point counts in `spectra_data` match
  what TRFP reads; (m/z,intensity) multiset within f32 tolerance. (Full mzML2mzPeak differential is Phase 4;
  a lightweight count/multiset check on small.RAW is enough here.)

## Reference artifacts

- `.planning/phases/01-walking-skeleton-.../01-01-SUMMARY.md` — helper API + runtime + array_index json.
- `refs/_findings/mzpeak_groundtruth_schema.md`; `refs/mzPeak/small.unpacked.mzpeak/` (has both data + peaks).
- `ThermoRawFileParser/Writer/ParquetSpectrumWriter.cs` (per-scan loop, centroid flag, batching).
