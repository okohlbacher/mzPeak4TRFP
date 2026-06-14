# Phase 3 plan review brief (plan gate)

Adversarial review of the PLAN. Read-only; findings only. This is the most complex phase.

## Context

Phase 3 turns the thin `spectra_metadata.parquet` (currently just a `spectrum` struct) into the full
packed-parallel set (spectrum/scan/precursor/selected_ion as CO-RESIDENT null-padded tables in one
parquet, linked by source_index/precursor_index fields — NOT row-disjoint), and populates the
`mzpeak_index.json` metadata{} block + Parquet footer cv_list. Research (RESEARCH.md) resolved the row
mechanics, Thermo-extraction reuse, CV values, and validator requirements.

## Read (repo root /Users/kohlbach/Claude/mzPeak4TRFR)

- .planning/phases/03-spectra-metadata-file-level-metadata-index/03-01-PLAN.md   ← THE PLAN
- .planning/phases/03-spectra-metadata-file-level-metadata-index/RESEARCH.md  (authoritative; corrects CONTEXT row model)
- .planning/phases/03-spectra-metadata-file-level-metadata-index/CONTEXT.md
- refs/_findings/mzpeak_groundtruth_schema.md ; refs/mzPeak/small.unpacked.mzpeak/ (real metadata facet + index)
- ThermoRawFileParser/Writer/MzPeakSpectrumWriter.cs, Writer/MzPeak/MzPeakParquet.cs (current code)
- ThermoRawFileParser/Writer/MzMlSpectrumWriter.cs, OntologyMapping.cs (reuse sources)

## Evaluate (focus on the hard parts)

1. CO-RESIDENT TABLE MECHANICS: Is the plan's row-emission algorithm (4 parallel struct columns, each at
   row=its ordinal, right-padded with nulls; scan full, precursor/selected_ion only for MSn) correct vs the
   ground truth? Will the null-padding + definition levels be expressed correctly with MzPeakParquet? Any
   off-by-one or row-count mismatch risk across the 4 columns in ONE row group?
2. LIST-OF-STRUCT (Task 1 risk): Is proving `large_list<struct>` (PARAM list, scan_windows) via a round-trip
   BEFORE building on it the right call? Is the repetition/definition-level handling specified concretely
   enough, or will the executor guess? Empty-list vs null-list distinction handled?
3. LINKAGE CORRECTNESS: source_index (scan→spectrum, precursor→MSn spectrum), precursor_index (→parent MS1
   ordinal), selected_ion linkage. Is the parent-MS1-ordinal lookup well-defined (the writer has a dense
   ordinal, but the Thermo precursor references a scan NUMBER — is the scan-number→ordinal map specified)?
4. CV CORRECTNESS: polarity int8 sign (not CURIE); representation/type CURIE; activation+CE as PARAM entries
   (CID=MS:1000133); scan_start_time raw minutes (no conversion, unit UO:0000031). Any column where the plan
   emits the wrong kind (scalar vs CURIE vs PARAM) or wrong unit?
5. CV_LIST COMPLETENESS: cv_list must be a superset of EVERY accession the writer emits. Is there a mechanism
   to guarantee that, or will it drift as columns are added?
6. VALIDATOR BAR: plan targets "scan-facet error gone + cv_list present + no NEW errors" (not a fully clean
   pass, since even the reference fails 2 rules). Is that bar stated and is the gate decisive about it?
7. REUSE/BLOAT: reuses MzMlSpectrumWriter extraction + OntologyMapping + MzPeakParquet without forking?
   Over/under-specification? Any instruction producing harness/process/phase comments?
8. ACCEPTANCE DECISIVENESS + runtime env (roll-forward build; DOTNET_ROOT_X64 tests; arch -x86_64 convert; mzpeak-validate) embedded in commands?

## Output

Prioritized findings: SEVERITY (BLOCKER/HIGH/MEDIUM/LOW/NIT), location, problem, concrete fix. End with one
line: VERDICT: SHIP / SHIP-WITH-FIXES / REWORK.
