# Phase 4 plan review brief (plan gate) — final phase

Adversarial review of the PLAN. Read-only; findings only.

## Context

Final phase: add TIC chromatogram facets and the conformance suite (validator stays 0/0; differential vs
mzML2mzPeak; L1/L2 round-trip; NUnit). RESEARCH.md resolved TIC extraction (1:1 with scans via
GetChromatogramData(TraceType.TIC)), ground-truth chromatogram shapes, validator behavior (no chromatogram
rule), and the differential pipeline (reference is chunk/delta+zlib, routes profile MS1→data centroid
MS2→peaks, has 0 chromatograms; nonzero (m/z,intensity) multiset already proven to match 11057==11057).

## Read (repo root /Users/kohlbach/Claude/mzPeak4TRFR)

- .planning/phases/04-chromatograms-conformance-verification/04-01-PLAN.md   ← THE PLAN
- .planning/phases/04-chromatograms-conformance-verification/RESEARCH.md (authoritative)
- .planning/phases/04-chromatograms-conformance-verification/CONTEXT.md
- refs/_findings/mzpeak_groundtruth_schema.md ; refs/mzPeak/small.unpacked.mzpeak/chromatograms_*.parquet
- ThermoRawFileParser/Writer/MzPeakSpectrumWriter.cs, Writer/MzPeak/MzPeakParquet.cs, Writer/MzMlSpectrumWriter.cs

## Evaluate

1. TIC CORRECTNESS: GetChromatogramData(TraceType.TIC) usage faithful to MzMlSpectrumWriter? time=minutes f64,
   ms_level=per-scan (not 0), intensity=device f32. Is the length-guard / re-key-by-scan-number fallback
   (open question A1) specified so a TIC-length≠scan-count mismatch can't silently misalign? Does the
   chromatogram point set stay consistent with the emitted spectrum set under filtering?
2. SCHEMA FIDELITY: chromatograms_metadata (id="TIC", scan_polarity int8=0, type MS:1000235, ndp, empty
   params/aux) + present-but-null precursor/selected_ion matching ground truth; chromatograms_data point
   struct order/types. chromatogram_array_index JSON correct. index files[] chromatogram entries.
3. VALIDATOR INVARIANT: does the plan correctly assert mzpeak-validate STAYS 0/0 after chromatograms (no new
   cv codes / no chromatogram rule), parsed decisively?
4. DIFFERENTIAL (VER-02) SOUNDNESS: is the comparison methodology valid — chunk decode (abs = chunk_start +
   cumsum(deltas)), facet-routing alignment (our dual vs reference profile/centroid split), --no-numpress,
   -f 4 (not 5), dropping the TIC (reference has 0 chromatograms)? Could the test pass spuriously (e.g.
   comparing empty sets) or fail on a legitimate-but-expected divergence? Is the prebuilt binary path used
   (no rebuild)? Is the comparison asserting the RIGHT invariant (nonzero multiset equality + counts)?
5. L1/L2 (VER-04): trusting our own spectra_data as post-read truth — is that circular (would it catch a real
   narrowing bug)? Or is it the right "no double-read" call? Flag if the L1/L2 assertion is vacuous.
6. ACCEPTANCE DECISIVENESS + runtime env in commands; reuse of MzPeakParquet (no fork); no harness/process/
   phase comments; BOM-free.

## Output

Prioritized findings: SEVERITY (BLOCKER/HIGH/MEDIUM/LOW/NIT), location, problem, concrete fix. End with one
line: VERDICT: SHIP / SHIP-WITH-FIXES / REWORK.
