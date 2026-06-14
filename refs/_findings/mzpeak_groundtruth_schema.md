# mzPeak ground-truth schema (extracted from refs/mzPeak/small.unpacked.mzpeak)

Authoritative Arrow/Parquet schemas of a real mzPeak archive produced by the HUPO-PSI Rust
writer. This is the *exact* target the TRFP C# writer must reproduce.

## Archive layout (uncompressed ZIP, `CompressionMethod::Stored`)

- `mzpeak_index.json` — `{ "files":[{name,entity_type,data_kind}], "metadata":{...} }`
- `spectra_metadata.parquet` — packed parallel tables (REQUIRED)
- `spectra_data.parquet` — point layout signal (REQUIRED)
- `spectra_peaks.parquet` — point layout centroids (OPTIONAL)
- `chromatograms_metadata.parquet` — (present when chromatograms exist)
- `chromatograms_data.parquet`

Per-Parquet ZSTD internal compression. CV terms encoded as `NS:accession` CURIEs; column
names embed the accession, e.g. `MS_1000511_ms_level`.

## spectra_data.parquet / spectra_peaks.parquet (point layout)

```
point: struct<spectrum_index: uint64, mz: double, intensity: float>
```

m/z = float64, intensity = float32. One row per data point. mz sorted ascending per spectrum.

## chromatograms_data.parquet

```
point: struct<chromatogram_index: uint64, time: double, intensity: float, ms_level: int64>
```

## spectra_metadata.parquet — packed parallel tables

Four independent TOP-LEVEL struct columns in one file. Each row populates exactly one;
the others are null. (`scan`/`precursor`/`selected_ion` reference back via `source_index`.)

### spectrum
```
index: uint64
id: large_string
MS_1000511_ms_level: uint8
time: double
MS_1000465_scan_polarity: int8
MS_1000525_spectrum_representation: string   (CURIE: MS:1000127 centroid / MS:1000128 profile)
MS_1000559_spectrum_type: string             (CURIE: MS:1000579 MS1 / MS:1000580 MSn)
MS_1000528_lowest_observed_mz_unit_MS_1000040: double
MS_1000527_highest_observed_mz_unit_MS_1000040: double
MS_1003060_number_of_data_points: uint64
MS_1003059_number_of_peaks: uint64
MS_1000504_base_peak_mz_unit_MS_1000040: double
MS_1000505_base_peak_intensity_unit_MS_1000131: float
MS_1000285_total_ion_current_unit_MS_1000131: float
data_processing_ref: large_string
parameters: large_list<PARAM>
auxiliary_arrays: large_list<AUX_ARRAY>
number_of_auxiliary_arrays: uint32
mz_delta_model: large_list<double>
```

### scan
```
source_index: uint64
scan_index: uint64
MS_1000016_scan_start_time_unit_UO_0000031: float   (UO:0000031 = minute)
MS_1000616_preset_scan_configuration: uint32
MS_1000512_filter_string: large_string
MS_1000927_ion_injection_time_unit_UO_0000028: float
ion_mobility_value: double
ion_mobility_type: string
instrument_configuration_ref: uint32
spectrum_reference: large_string
parameters: large_list<PARAM>
scan_windows: large_list<struct<
    MS_1000501_scan_window_lower_limit_unit_MS_1000040: float,
    MS_1000500_scan_window_upper_limit_unit_MS_1000040: float,
    parameters: large_list<PARAM>>>
```

### precursor
```
source_index: uint64
precursor_index: uint64
precursor_id: large_string
isolation_window: struct<
    MS_1000827_isolation_window_target_mz: float,
    MS_1000828_isolation_window_lower_offset: float,
    MS_1000829_isolation_window_upper_offset: float,
    parameters: large_list<PARAM>>
activation: struct<parameters: large_list<PARAM>>
```

### selected_ion
```
source_index: uint64
precursor_index: uint64
MS_1000744_selected_ion_mz_unit_MS_1000040: double
MS_1000041_charge_state: int32
MS_1000042_intensity_unit_MS_1000131: float
ion_mobility_value: double
ion_mobility_type: string
parameters: large_list<PARAM>
```

## chromatograms_metadata.parquet

```
chromatogram: struct<index, id, MS_1000465_scan_polarity:int8,
    MS_1000626_chromatogram_type:string, data_processing_ref,
    MS_1003060_number_of_data_points:uint64, parameters, auxiliary_arrays,
    number_of_auxiliary_arrays>
precursor: struct<...>        (same shape as spectra precursor)
selected_ion: struct<...>     (same shape as spectra selected_ion)
```

## PARAM (the repeated nested value type)

```
struct<
  value: struct<integer:int64, float:double, string:large_string, boolean:bool>,  (union-as-struct, one set)
  accession: string,   (CURIE, nullable)
  name: large_string,
  unit: string>        (CURIE, nullable)
```

## AUX_ARRAY
```
struct<data: large_list<uint8 not null>, name: PARAM, data_type: string,
       compression: string, unit: string, parameters: large_list<PARAM>,
       data_processing_ref: large_string>
```

## mzpeak_index.json metadata block (observed keys)
`scan_settings_list`, `instrument_configuration_list` (components: ionsource/analyzer/detector
with order + CV parameters), plus file_description / software_list / data_processing_method_list /
sample_list / cv_list per the schema/ JSONSchemas.

## Implementation notes / risks
- Parquet.Net v5.0.1 is already a TRFP dependency. The existing ParquetSpectrumWriter uses the
  high-level `ParquetSerializer.SerializeAsync` over a *flat* struct. mzPeak needs **nested
  structs + lists-of-structs + parallel nullable top-level columns** — likely requires the
  low-level Parquet.Net schema/column API (`ParquetSchema`, `DataColumn`, struct/list fields),
  not the POCO serializer. This is the primary technical risk → warrants an early spike.
- `large_string`/`large_list` = Arrow large variants (64-bit offsets). Confirm Parquet.Net maps
  plain string/list compatibly (readers likely accept both); verify with a round-trip read.
- ZIP must be STORED (no deflate) at the archive level.
