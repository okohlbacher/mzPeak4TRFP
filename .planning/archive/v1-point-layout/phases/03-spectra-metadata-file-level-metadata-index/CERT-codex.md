HIGH, ThermoRawFileParser/Writer/MzPeak/MzPeakParquet.cs:87, `NullList()` always becomes def-level 0, so it cannot represent a null list whose parent structs are present; it instead encodes the path as absent from the root. Fix: make null-list rows level-aware from the schema/list ancestor, reserve def=0 for padded top-level absence, and add a pyarrow assertion that distinguishes `activation != null && parameters == null` from `activation == null`.

MEDIUM, ThermoRawFileParser/ThermoRawFileParserTest/MzPeakWriterTests.cs:453, the linkage test only checks `precursor.source_index` is sorted, not that it equals the exact MSn ordinal set, and it does not check `selected_ion.precursor_index == precursor.precursor_index`. Fix: compute `msnOrdinals` from `spectrum.MS_1000511_ms_level >= 2`, assert `srcs == msnOrdinals`, and assert both selected-ion linkage fields mirror precursor per row.

LOW, ThermoRawFileParser/ThermoRawFileParserTest/MzPeakWriterTests.cs:650, the cv_list test asserts only a superset of CVs found in column names, so hard-coded extras or missing PARAM-only/footer-only prefixes would pass. Fix: recursively collect `accession` and `unit` prefixes from metadata JSON/footer blocks and PARAM lists, then assert exact equality for index and footer cv_list IDs.

LOW, ThermoRawFileParser/Writer/MzPeakSpectrumWriter.cs:420, dead/unused code remains (`msnAtRow`; also unused `AddScalar` arg at :582, unused `present` arg at :787, unused `enc` at :860). Fix: remove these to keep the large writer tight.

NIT, ThermoRawFileParser/ThermoRawFileParserTest/MzPeakParquetTests.cs:267, a phase/process reference remains in a comment (“Phase-3 facet shapes”). Fix: replace with domain wording such as “real mzPeak spectra-metadata facet shapes.”

VERDICT: CERTIFY-WITH-FIXES
