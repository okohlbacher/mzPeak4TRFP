# Phase 3 certification review brief (close gate)

Adversarial review of the IMPLEMENTED Phase 3 code. Read-only; findings only.

## What shipped

`spectra_metadata.parquet` now has the four co-resident packed-parallel tables (spectrum/scan/
precursor/selected_ion, null-padded), built on a general nested def/rep-level computer; `mzpeak_index.json`
metadata{} + Parquet footer carry cv_list (generated), instrument_configuration_list (OntologyMapping),
software_list, data_processing_method_list, file_description. **`mzpeak-validate` → PASS (0 errors, 0
warnings).** 44/44 tests pass.

## Read (repo root /Users/kohlbach/Claude/mzPeak4TRFR)

- ThermoRawFileParser/Writer/MzPeakSpectrumWriter.cs        ← main writer (now large)
- ThermoRawFileParser/Writer/MzPeak/MzPeakParquet.cs        ← helper incl. NestedLevels computer + list-of-struct
- ThermoRawFileParser/ThermoRawFileParserTest/  (Phase-3 NUnit tests)
- .planning/phases/03-spectra-metadata-file-level-metadata-index/03-01-PLAN.md, RESEARCH.md, 03-01-SUMMARY.md
- refs/_findings/mzpeak_groundtruth_schema.md ; refs/mzPeak/small.unpacked.mzpeak/ (reference to diff)

## Evaluate

1. CORRECTNESS: the NestedLevels def/rep-level computer — is it correct for all shapes (nested struct
   present/null, list null/empty/N, leaf null, null-padded tail)? Any off-by-one in def/rep levels that
   would silently corrupt nullity even though the validator passed? Parent linkage (scanNumber→ordinal,
   Master Scan Number, filtered-parent → null precursor_index). Resource disposal.
2. SCHEMA FIDELITY vs ground truth: column names/types/CURIEs/list-item naming match
   refs/mzPeak/small.unpacked.mzpeak (spot-diff with pyarrow if useful). polarity int8 sign; representation/
   type CURIEs; activation+CE PARAM entries; scan_start_time minutes. Anything that passes mzpeak-validate
   but diverges from the reference in a way a stricter reader would reject?
3. CV_LIST INTEGRITY: is cv_list truly a superset of every accession emitted (no drift), generated not
   hard-coded? instrument_configuration_list component order + CV params sane for the small.RAW instrument?
4. DUAL-FACET COUNTS: number_of_data_points vs nullable number_of_peaks correct per ordinal.
5. BLOAT/STYLE: the writer grew a lot — dead code, duplication (esp. anything that should reuse MzPeakParquet/
   OntologyMapping/MzMlSpectrumWriter rather than reimplement), over-engineering, redundant comments, and any
   comment referencing harness/process/phases (forbidden) — exact lines. BOM on new files.
6. TEST QUALITY: do the tests actually lock null discipline, linkage, CV values, list/item path, and the
   nullable count — or are any vacuous? Is the validator gate parsed by error-id as intended?

## Output

Prioritized findings: SEVERITY (BLOCKER/HIGH/MEDIUM/LOW/NIT), file:line, problem, concrete fix. End with one
line: VERDICT: CERTIFY / CERTIFY-WITH-FIXES / REWORK.
