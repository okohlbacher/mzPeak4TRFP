# Phase 4 Context: Chromatograms + Conformance Verification

**Requirements:** CHROM-01, CHROM-02, VER-01..04
**Depends on:** Phase 3 (full spectra facets + index metadata; archive currently PASSES mzpeak-validate 0/0).

## Intent

Add the TIC chromatogram facets and lock conformance with an automated verification suite: the
`mzpeak-validate` gate (must STAY 0/0 after chromatograms), a differential equivalence check vs the
`mzML2mzPeak` reference converter, and an L1/L2 round-trip. This is the closing phase — it certifies
the whole writer end-to-end.

## Decisions (locked)

- **`chromatograms_data.parquet`** point layout `point: struct<chromatogram_index:u64, time:f64,
  intensity:f32, ms_level:i64>` carrying the run's TIC (chromatogram_index 0). Time in MINUTES (Thermo
  native), float64; intensity float32.
- **`chromatograms_metadata.parquet`** = the `chromatogram` struct (per ground truth: index, id,
  `MS_1000465_scan_polarity` int8, `MS_1000626_chromatogram_type` CURIE = MS:1000235 total ion current
  chromatogram, data_processing_ref, `MS_1003060_number_of_data_points` u64, parameters, auxiliary_arrays,
  number_of_auxiliary_arrays) + the co-resident `precursor`/`selected_ion` structs (null for the TIC —
  match the ground-truth shape even though empty). One row (the TIC).
- **TIC source:** reuse Thermo's chromatogram API the way the existing writers do — get the device TIC via
  `rawFile.GetChromatogramData(...)` with a TIC trace setting over the scan range (research the exact call;
  MzMlSpectrumWriter / the chromatogram path is the reuse source). Do NOT recompute TIC by summing spectra
  if the instrument exposes it; if it doesn't, fall back to summing per-scan TIC (already read in Phase 3).
- **Index:** add the two chromatogram facets to `mzpeak_index.json` files[] (entity_type "chromatogram",
  data_kind "data arrays" / "metadata"). Reader requires the chromatogram metadata facet to exist when
  any chromatogram is present.
- Reuse `MzPeakParquet` (point writer + NestedLevels + cv_list collection) — extend, don't fork. Any new
  CV accession (MS:1000235, MS:1000626, time unit) MUST flow into the generated cv_list.

## Verification suite (the heart of this phase)

- **VER-01 (validator):** `mzpeak-validate <out>.mzpeak` → PASS (0 errors). Must remain 0/0 after adding
  chromatograms (watch for new chromatogram-facet rules / cv_list additions).
- **VER-02 (differential vs mzML2mzPeak):** Build the reference path for the SAME input and compare:
  - Reference: `small.RAW → (TRFP mzML writer, PROFILE mode to align with our spectra_data) → small.mzML
    → (mzml2mzpeak from ~/Claude/mzML2mzPeak, `cargo run --release` or the built binary) → small.ref.mzpeak`.
  - Compare via pyarrow: spectrum count; per-spectrum point counts; (m/z, intensity) multiset within f32
    tol; ms_level; polarity; RT; precursor m/z/charge; TIC. Document any EXPECTED divergence (e.g. our dual
    spectra_peaks vs the reference's facet routing) rather than forcing bit-equality. The goal is semantic
    equivalence of the spectral content, not byte-identity.
- **VER-04 (L1/L2 round-trip):** read our `.mzpeak` back (pyarrow) and compare to what TRFP read from the
  RAW: m/z value-equal (L1, exact f64) and intensity bounded-equal under the recorded f32-narrowing
  transform (L2). Mirror mzML2mzPeak's conformance framing.
- **VER-03 (NUnit):** a test that converts `ThermoRawFileParserTest/Data/small.RAW` to mzPeak, asserts
  archive structure (all expected facets, chromatograms present) and invokes `mzpeak-validate`
  (skip-with-warning if the validator/python is unavailable in the test env).

## Constraints / runtime

- Build: roll-forward env; the x64 DLL is at `bin/x64/Release/net8.0/`; run via `arch -x86_64
  ~/.dotnet-x64/dotnet ...`; tests `DOTNET_ROOT_X64=$HOME/.dotnet-x64 dotnet test`; validator `mzpeak-validate`.
- mzML2mzPeak: at `~/Claude/mzML2mzPeak` (Rust, `cargo build --release` → target/release/mzml2mzpeak; rust-toolchain pinned 1.96). Build once; reuse the binary.
- m/z f64, intensity f32; STORED zip + ZSTD; compact code; explicit usings; BOM-free new files; NO comments
  referencing harness/process/phases.

## Reference artifacts

- `refs/_findings/mzpeak_groundtruth_schema.md` (chromatograms_metadata / chromatograms_data schema).
- `refs/mzPeak/small.unpacked.mzpeak/chromatograms_*.parquet` (real reference to diff).
- `~/Claude/mzML2mzPeak` (reference converter for VER-02); `~/Claude/mzPeakValidator` (VER-01/03).
- `.planning/phases/03-.../03-01-SUMMARY.md` (writer/helper state, cv_list mechanism, runtime).
- `ThermoRawFileParser/Writer/MzMlSpectrumWriter.cs` (chromatogram extraction reuse).
