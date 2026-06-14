MEDIUM [MzPeakSpectrumWriter.cs](/Users/kohlbach/Claude/mzPeak4TRFR/ThermoRawFileParser/Writer/MzPeakSpectrumWriter.cs:810): `selected_ion` does not match the referenced ground-truth shape: the reference has `ion_mobility_value`, `ion_mobility_type`, and `parameters`, but the builder emits only five fields. Fix by either adding the all-null fields consistently, or narrowing the certification/docs from “ground-truth schema” to the documented v1 simplified shape.

MEDIUM [MzPeakWriterTests.cs](/Users/kohlbach/Claude/mzPeak4TRFR/ThermoRawFileParser/ThermoRawFileParserTest/MzPeakWriterTests.cs:1103): L2 value-equality is over-broadly attributed to VER-02, but VER-02 only compares the 14 reference-profile spectra and scopes centroid/MS2 out. Fix by qualifying the claim to that subset, or add an independent intensity-value check for the remaining emitted spectra.

LOW [MzPeakSpectrumWriter.cs](/Users/kohlbach/Claude/mzPeak4TRFR/ThermoRawFileParser/Writer/MzPeakSpectrumWriter.cs:250): `cv_list` is finalized in `BuildMetadataFacet` before chromatogram builders collect their CURIE prefixes, so the “route through `CollectPrefix` for exhaustive cv coverage” invariant is currently accidental because all new prefixes are already `MS/UO`. Fix by collecting chromatogram prefixes before `AddMetadataBlocks`, or finalizing metadata blocks after all facets are built.

NIT [MzPeakWriterTests.cs](/Users/kohlbach/Claude/mzPeak4TRFR/ThermoRawFileParser/ThermoRawFileParserTest/MzPeakWriterTests.cs:1183): forbidden phase/process comments remain (`VER-02`, `VER-03` at lines 1103, 1183, 1201). Fix by rephrasing as domain rationale without phase IDs.

VERDICT: CERTIFY-WITH-FIXES
