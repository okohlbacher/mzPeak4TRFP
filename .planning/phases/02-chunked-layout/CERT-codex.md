HIGH, ThermoRawFileParser/Writer/MzPeak/MzPeakChunkCodec.cs:103, `Chunk` accepts non-finite/non-positive widths; `width <= 0` can infinite-loop at line 124, and programmatic `ParseInput.MzPeakChunkSize` reaches this path unchecked. Fix with a finite `> 0` guard in the codec/facet and CLI parse.

HIGH, ThermoRawFileParser/ThermoRawFileParserTest/MzPeakDifferentialTests.cs:101, the differential test forces TRFP to `--point` at line 183, so it is not KeyError-skipped, but its reference chunk decoder is not null-aware (`mz += d` fails on `None`). The reference fixture has null-marked chunk rows. Fix by using the same null-aware decode logic as `MzPeakChunkCodec`.

MEDIUM, ThermoRawFileParser/ThermoRawFileParserTest/MzPeakWriterTests.cs:39, the shared test converter forces `MzPeakPointLayout = true`, so `DataFacet_MultiRowGroup_Equals_SingleRowGroup` does not exercise chunk list-column row-group flushing. Add a chunk-specific lowered-cap test that decodes all row groups and compares against single-row-group chunked output.

NIT, ThermoRawFileParser/Writer/MzPeakSpectrumWriter.cs:34, production code contains forbidden test-hook comments; same issue at lines 156 and 162. Remove or rephrase without harness/test wording.

VERDICT: CERTIFY-WITH-FIXES
