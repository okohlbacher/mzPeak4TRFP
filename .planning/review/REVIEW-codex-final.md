**Findings**

- HIGH: footer counts are stale/hard-coded to zero. [MzPeakMetadataFacetBuilder.cs](/Users/kohlbach/Claude/mzPeak4TRFR/ThermoRawFileParser/Writer/MzPeak/MzPeakMetadataFacetBuilder.cs:122) emits `spectrum_data_point_count = "0"` for `spectra_metadata.parquet`; [MzPeakSpectrumWriter.cs](/Users/kohlbach/Claude/mzPeak4TRFR/ThermoRawFileParser/Writer/MzPeakSpectrumWriter.cs:185) emits `chromatogram_data_point_count = "0"` for `chromatograms_data.parquet`; [MzPeakChromatogramBuilder.cs](/Users/kohlbach/Claude/mzPeak4TRFR/ThermoRawFileParser/Writer/MzPeak/MzPeakChromatogramBuilder.cs:43) emits the same zero for `chromatograms_metadata.parquet`. Pyarrow on `refs/mzPeak/small.numpress.mzpeak` shows nonzero reference footers: `spectra_metadata.parquet` has `spectrum_data_point_count=488`, `chromatograms_metadata.parquet` has `chromatogram_data_point_count=1`, and `chromatograms_data.parquet` has `chromatogram_data_point_count=1`. This is validator-visible and should be fixed.

**Confirmed Resolved**

The prior BLOCKERs for `spectrum`/`scan` missing columns and nullable absent values are resolved in the current code. Pyarrow against `refs/mzPeak/small.numpress.mzpeak` confirms the reference has `spectrum=19` and `scan=12`; the current `BuildSpectrumField()` / `BuildScanField()` declarations match name and order at [MzPeakMetadataFacetBuilder.cs](/Users/kohlbach/Claude/mzPeak4TRFR/ThermoRawFileParser/Writer/MzPeak/MzPeakMetadataFacetBuilder.cs:133).

Nullability is now represented with nullable staged fields plus leaf-null emission, not zero-fill: see [ScanStager.cs](/Users/kohlbach/Claude/mzPeak4TRFR/ThermoRawFileParser/Writer/MzPeak/ScanStager.cs:77) and [MzPeakColumns.cs](/Users/kohlbach/Claude/mzPeak4TRFR/ThermoRawFileParser/Writer/MzPeak/MzPeakColumns.cs:55). Empty in-range spectra keep a metadata row and only skip data/peak facet rows at [MzPeakSpectrumWriter.cs](/Users/kohlbach/Claude/mzPeak4TRFR/ThermoRawFileParser/Writer/MzPeakSpectrumWriter.cs:133). Provenance is now `MS:1000530` via [MzPeakMetadataBlocks.cs](/Users/kohlbach/Claude/mzPeak4TRFR/ThermoRawFileParser/Writer/MzPeak/MzPeakMetadataBlocks.cs:141).

One nuance: the reference does not row-align `precursor`/`selected_ion` to `spectrum` MS1 rows. It packs MSn precursor rows first and null-pads the tail; the current code follows that model.

I could not run a fresh writer conversion or `mzpeak-validate` in this session because the sandbox is read-only and both .NET/validator need a writable temp directory. The documented deviations, intensity SLOF out of scope and Arrow field metadata via footer, remain acceptable.

VERDICT: FIXES-NEEDED
