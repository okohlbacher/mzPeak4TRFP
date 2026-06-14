# Phase 4 certification review brief (close gate — final phase)

Adversarial review of the IMPLEMENTED Phase 4 code. Read-only; findings only.

## What shipped

TIC `chromatograms_data` (point: chromatogram_index u64, time f64, intensity f32, ms_level i64) +
`chromatograms_metadata` (chromatogram struct id=TIC type MS:1000235 + present-but-null precursor/
selected_ion + aux_array shape) via Thermo GetChromatogramData(TraceType.TIC), 1:1 with scans. Index +
footer updated. Verification: mzpeak-validate PASS 0/0 WITH chromatograms; differential vs mzML2mzPeak
(exact profile multiset 11057==11057 across 14 spectra); L1 m/z bit-exact vs independent RAW re-read
(L2 value-equality via VER-02). 51/51 tests.

## Read (repo root /Users/kohlbach/Claude/mzPeak4TRFR)

- ThermoRawFileParser/Writer/MzPeakSpectrumWriter.cs   ← TIC emission added
- ThermoRawFileParser/Writer/MzPeak/MzPeakParquet.cs   ← chromatogram/aux field builders
- ThermoRawFileParser/ThermoRawFileParserTest/  (MzPeakWriterTests, MzPeakDifferentialTests)
- .planning/phases/04-chromatograms-conformance-verification/04-01-PLAN.md, RESEARCH.md, 04-01-SUMMARY.md, REVIEW-SYNTHESIS.md
- refs/_findings/mzpeak_groundtruth_schema.md ; refs/mzPeak/small.unpacked.mzpeak/chromatograms_*.parquet

## Evaluate

1. TIC CORRECTNESS: GetChromatogramData usage; time(min)/ms_level(per-scan)/intensity(device) mapping;
   the empty-trace branch (no trace[0] deref); device-vs-summed source flag honest; chromatogram point set
   consistent with the emitted spectrum set under filtering. Resource disposal.
2. SCHEMA FIDELITY: chromatograms_metadata matches ground truth (chromatogram struct fields/types/CURIEs,
   present-but-null precursor/selected_ion, aux_array shape); chromatograms_data struct order/types; index
   files[] entries; footer keys (chromatogram_count on metadata only, not data).
3. VALIDATOR INVARIANT: still PASS 0/0 with chromatograms, asserted by error-id; cv_list unchanged/correct.
4. DIFFERENTIAL TEST QUALITY (VER-02): is the comparison decisive (exact compared index set == 14, nonzero
   multiset equality, chunk decode correct) or can it pass spuriously? Is the centroid scope-out honest?
5. L1/L2 (VER-04): is the L1 independent re-read genuinely non-circular? Is the L2 downgrade-to-structural
   honestly scoped (value equality resting on VER-02) and documented — or does it overclaim?
6. BLOAT/STYLE: dead code, duplication (reuse MzPeakParquet/OntologyMapping/MzMlSpectrumWriter rather than
   reimplement), redundant comments, any harness/process/phase comment (forbidden) — exact lines; BOM on new files.
7. WHOLE-WRITER SANITY: now that all facets exist, is MzPeakSpectrumWriter coherent or has it accreted
   cruft worth a small cleanup? (Flag only concrete issues.)

## Output

Prioritized findings: SEVERITY (BLOCKER/HIGH/MEDIUM/LOW/NIT), file:line, problem, concrete fix. End with one
line: VERDICT: CERTIFY / CERTIFY-WITH-FIXES / REWORK.
