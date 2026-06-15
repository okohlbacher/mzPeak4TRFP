# Full adversarial code review brief — mzPeak writer (our code only)

Review ONLY the code WE authored for the mzPeak output format. EXCLUDE the vendored upstream
ThermoRawFileParser / OpenMS code (MzMlSpectrumWriter, MgfSpectrumWriter, ParquetSpectrumWriter,
OntologyMapping, the Thermo reader wrappers, etc. — those are not ours to review).

Read-only. Output prioritized findings only.

## In-scope files (repo root /Users/kohlbach/Claude/mzPeak4TRFR)

- ThermoRawFileParser/Writer/MzPeakSpectrumWriter.cs   (2006 LOC — the main writer; does point+chunk+numpress data facets, packed-parallel metadata, index json, chromatograms, streaming, robustness)
- ThermoRawFileParser/Writer/MzPeak/MzPeakParquet.cs   (low-level Parquet helper: schema/columns/NestedLevels/streaming Handle/NullList)
- ThermoRawFileParser/Writer/MzPeak/MzPeakChunkCodec.cs (chunk windowing + delta encode/decode)
- ThermoRawFileParser/Writer/MzPeak/MSNumpress.cs      (vendored C# numpress-linear port)
- ThermoRawFileParser/ThermoRawFileParserTest/MzPeak*.cs (tests)
- tools/e2e/compare_mzpeak.py, run_corpus_e2e.py
- Our wiring edits in OutputFormat.cs, RawFileParser.cs, MainClass.cs, ParseInput.cs, Writer/SpectrumWriter.cs

## Authoritative references for correctness
- refs/_findings/mzpeak_groundtruth_schema.md (target Arrow/Parquet schema)
- refs/_findings/mzpeak_mapping_report.md (mzML-CV → mzPeak mapping bible)
- refs/mzPeak/small.{unpacked,chunked,numpress}.mzpeak (reference archives; pyarrow-dump to verify)

## Review dimensions (report findings under each)

1. **CORRECTNESS of data mappings.** Verify every CV term / column the writer emits against the ground-truth
   schema + mapping bible: ms_level, polarity (int8 sign), representation/spectrum_type CURIEs, scan_start_time
   (minutes/UO:0000031), ion injection time, isolation window (target/lower/upper), activation (CID MS:1000133
   etc. + collision energy), selected_ion (mz/charge/intensity), base peak, TIC, observed m/z range, counts.
   Flag any wrong accession, unit, type, sign, or column name. Verify numpress (MS:1002312) + delta (MS:1003089)
   + array_index transforms + cv_list completeness. Verify the precursor→parent linkage (source_index/precursor_index).

2. **FEATURE COMPLETENESS & CORRECTNESS.** Does the writer correctly implement: point/chunk/numpress modes +
   precedence (--point > --lossless > numpress-default); spectra_data + spectra_peaks (dual representation) +
   chromatograms (TIC); packed-parallel metadata (spectrum/scan/precursor/selected_ion); the index metadata
   block (instrument_configuration/software/data_processing/file_description/cv_list); streaming + per-scan
   robustness? Any half-implemented or incorrect feature? MS-level/scan-range filter handling?

3. **SEPARATION OF CONCERNS / maintainability.** The 2006-line MzPeakSpectrumWriter is a smell — assess whether
   it should be decomposed (e.g. facet writers, metadata builder, index builder, CV/param helpers) and HOW,
   without risking the verified behavior. Flag duplication, god-methods, mixed abstraction levels, leaky
   coupling. Distinguish "worth refactoring now" from "acceptable".

4. **TEST COVERAGE & EDGE CASES.** What's untested or weakly tested? Edge cases: empty spectrum, single-point
   spectrum, zero-intensity/all-zero, MS2-only filtering, missing precursor/charge, FAIMS, negative polarity,
   very large file (row-group boundaries), non-monotonic m/z, numpress short streams, null vs empty lists.
   Flag vacuous/tautological tests. Is the differential/L2 gate decisive?

5. **BLOAT / leakage.** Dead code, over-engineering, redundant comments, and any comment referencing
   GSD/AI/process/phases/certification/"reference dump"/"Option B"/research — flag exact file:line (we will strip).

## Output

Prioritized findings: SEVERITY (BLOCKER/HIGH/MEDIUM/LOW/NIT), dimension, file:line, problem, concrete fix.
Be concrete and honest — this drives real fixes. End with: top 5 fixes to make now.
