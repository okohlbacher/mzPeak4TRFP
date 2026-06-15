- BLOCKER, CORRECTNESS/data mappings, `ThermoRawFileParser/Writer/MzPeakSpectrumWriter.cs:1365`: `spectrum` metadata omits reference fields `data_processing_ref`, `auxiliary_arrays`, `number_of_auxiliary_arrays`, and `mz_delta_model`; pyarrow on all `refs/mzPeak/small.*.mzpeak` shows them present. Concrete fix: extend `BuildSpectrumField()` and `BuildMetadataFacet()` to emit these as nullable/null or empty values exactly matching the reference schema.

- BLOCKER, CORRECTNESS/data mappings, `ThermoRawFileParser/Writer/MzPeakSpectrumWriter.cs:1385`: `scan` metadata omits `MS_1000616_preset_scan_configuration`, `ion_mobility_value`, `ion_mobility_type`, `spectrum_reference`, `parameters`, and `scan_windows.parameters`; reference row 0 includes preset scan config and a `MS:1000800` scan parameter. Concrete fix: add the missing scan fields and populate known Thermo values, otherwise emit proper null/empty-list levels.

- BLOCKER, FEATURE COMPLETENESS/numpress, `ThermoRawFileParser/Writer/MzPeakSpectrumWriter.cs:847`: default numpress `spectra_data` schema is not the reference schema. The reference has `mz_numpress_linear_bytes` and `intensity_numpress_slof_bytes` with transforms `MS:1002312` and `MS:1002314`; our code keeps plain `chunk.intensity` and has no SLOF path. Concrete fix: implement SLOF intensity encoding and change schema/footer order to match `small.numpress.mzpeak`.

- HIGH, CORRECTNESS/array metadata, `ThermoRawFileParser/Writer/MzPeakSpectrumWriter.cs:823`: signal fields are created without Arrow field metadata, while the reference pyarrow schema carries `array_accession`, `data_type_accession`, `unit`, `buffer_format`, `transform`, etc. Concrete fix: attach field metadata or switch the low-level writer to one that can emit the Arrow metadata; add pyarrow assertions against refs.

- HIGH, CORRECTNESS/metadata nullability, `ThermoRawFileParser/Writer/MzPeakSpectrumWriter.cs:426`: optional values are forced to real values: unknown polarity becomes `+1`, missing ion injection time becomes `0`, missing base peak becomes `0`, and missing precursor details can become zero-valued fields. Concrete fix: make these `Record` members nullable and emit leaf-null definition levels.

- HIGH, CORRECTNESS/precursor mapping, `ThermoRawFileParser/Writer/MzPeakSpectrumWriter.cs:733`: `BuildPrecursor()` sets `IsMsn = true` before proving a reaction exists; if `GetReaction()` returns null, downstream code still emits `isolation_window_target_mz` and `selected_ion_mz` as default zero. Concrete fix: either skip detailed precursor/selected-ion fields or emit nullable fields when no reaction/selected ion exists.

- HIGH, FEATURE COMPLETENESS/edge cases, `ThermoRawFileParser/Writer/MzPeakSpectrumWriter.cs:386`: empty spectra are dropped entirely, losing spectrum metadata, ordinal continuity against source scans, and possible parent links. Concrete fix: emit a metadata row with zero data/peak counts and no data rows instead of returning null.

- MEDIUM, CORRECTNESS/schema exactness, `ThermoRawFileParser/Writer/MzPeak/MzPeakParquet.cs:25`: reference schemas use `large_string`/`large_list`; our Parquet.Net `DataField<string>`/`ListField` path likely writes regular UTF8/list. Concrete fix: verify generated output with pyarrow and either configure large logical types or document and test accepted compatibility.

- MEDIUM, CORRECTNESS/array_index, `ThermoRawFileParser/Writer/MzPeakSpectrumWriter.cs:62`: point `spectrum_array_index` lacks the reference `transform`/`data_processing_id` entries for `spectra_data`, while `spectra_peaks` should not blindly share the same index contract. Concrete fix: split array-index constants per facet and match the reference footers for point data versus peaks.

- MEDIUM, FEATURE COMPLETENESS/chromatograms, `ThermoRawFileParser/Writer/MzPeakSpectrumWriter.cs:857`: code locks chromatograms to point layout, but `refs/mzPeak/small.numpress.mzpeak` uses chunked chromatogram data with `intensity_numpress_slof_bytes`; tests explicitly assert point “by design.” Concrete fix: decide the spec, then align default numpress chromatogram output and tests to the authoritative reference or update the reference brief.

- MEDIUM, CORRECTNESS/provenance, `ThermoRawFileParser/Writer/MzPeakSpectrumWriter.cs:1845`: mzPeak output records `MS:1000544` “Conversion to mzML,” which is false for direct RAW→mzPeak output. Concrete fix: use a mzPeak/TRFP conversion processing method and wire numpress processing IDs consistently into array metadata.

- MEDIUM, TEST COVERAGE, `ThermoRawFileParser/ThermoRawFileParserTest/MzPeakWriterTests.cs:39`: most writer tests force `MzPeakPointLayout = true`, so the default numpress output path is under-tested. Concrete fix: run the same schema/value gates over point, lossless chunk, and numpress default.

- MEDIUM, TEST COVERAGE, `ThermoRawFileParser/ThermoRawFileParserTest/MzPeakDifferentialTests.cs:193`: the differential gate compares reference `--no-numpress` to our `--point`, skipping the actual default and much centroid coverage. Concrete fix: add pyarrow-driven differential checks against `small.unpacked`, `small.chunked`, and `small.numpress` for schemas, footers, and decoded values.

- LOW, MAINTAINABILITY, `ThermoRawFileParser/Writer/MzPeakSpectrumWriter.cs:21`: 2006-line writer mixes RAW extraction, precursor inference, Parquet schema/levels, streaming, chromatograms, index JSON, and provenance. Concrete fix: split into metadata builders, facet writers, index builder, CV/param helpers, and scan extraction without changing verified behavior.

- LOW, BLOAT/leakage, `tools/e2e/compare_mzpeak.py:58`: strip process/research comments: also `ThermoRawFileParser/ThermoRawFileParserTest/MzPeakWriterTests.cs:1524`, `:1735`, `:1738`, `:1756`, `ThermoRawFileParser/ThermoRawFileParserTest/MzPeakChunkTests.cs:441`, `:673`, `:982`. Concrete fix: replace with neutral technical comments or delete.

top 5 fixes to make now: complete `spectrum`/`scan` metadata schemas; fix numpress schema with SLOF intensity; add field metadata and correct array_index footers; make optional metadata nullable instead of zero-filled; add three-mode pyarrow reference-schema gates.
