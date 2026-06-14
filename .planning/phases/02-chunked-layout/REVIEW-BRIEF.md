# v2 Phase 2 plan review brief (plan gate)

Adversarial review of the PLAN. Read-only; findings only.

## Context

Phase 2 = emit `spectra_data` in the reference CHUNK layout (new default; `--point` opt-out), lossless
delta, reusing the Phase-1 streaming handle. `spectra_peaks` + `chromatograms_data` stay POINT.
RESEARCH (verified vs `refs/mzPeak/small.chunked.mzpeak` + a live Parquet.Net spike) established: 6-field
chunk struct (NO mz_numpress_linear_bytes in delta mode), chunk_encoding is a CURIE, intensity f32 list,
list columns stream via the existing NestedLevels/Handle. Phase 2 keeps ALL points (no zero-nulling →
exact lossless multiset vs v1; zero-stripping is Phase 4).

## Read (repo root /Users/kohlbach/Claude/mzPeak4TRFR)

- .planning/phases/02-chunked-layout/02-01-PLAN.md   ← THE PLAN
- .planning/phases/02-chunked-layout/RESEARCH.md (authoritative — supersedes CONTEXT/ROADMAP on the 6-field schema)
- .planning/phases/02-chunked-layout/CONTEXT.md
- refs/mzPeak/small.chunked.mzpeak (the reference; pyarrow-dump to check claims), refs/mzPeak/schema/array_index.json
- ThermoRawFileParser/Writer/MzPeakSpectrumWriter.cs, Writer/MzPeak/MzPeakParquet.cs (streaming Handle + NestedLevels)

## Evaluate

1. ENCODING CORRECTNESS: is the delta encode/decode exactly invertible for ALL inputs (duplicate m/z,
   single-point spectrum, empty spectrum, non-monotonic guard)? Does mz_chunk_start/end + mz_chunk_values
   match the reference's actual semantics (dump small.chunked.mzpeak to verify the worked example)? Is the
   chunk_encoding CURIE taken from the file, not hardcoded from memory?
2. LOSSLESS vs v1 (no regression): does Phase 2 truly keep ALL points (no zero-nulling) so chunked decode ==
   v1 point (m/z,intensity) multiset per spectrum EXACTLY? Any place the codec could drop/merge points or
   reorder within a chunk? Is the per-spectrum multiset-equality test decisive (not tautological)?
3. CHUNKING: window definition (first-mz-anchored ≤50 vs floor(mz/50)) — does it match the reference and
   tile the axis without gaps/overlaps/dropped points? Empty windows omitted correctly? Points crossing a
   window boundary handled? Default 50.0 + `--chunk-size` flag plumbed (ParseInput/MainClass)?
4. LIST-COLUMN STREAMING: rep/def levels for list<double>/list<float> via NestedLevels (leaf path/MaxDef/
   MaxRep from the spike) correct under row-group flushing? A chunk row is one row — confirm chunks aren't
   split across row groups in a way that corrupts the lists. Disposal/temp-file path reused from Phase 1.
5. array_index + cv_list: chunk buffer formats + transform CURIEs + sorting_rank:0 correct vs
   schema/array_index.json and the reference footer? Every new CURIE registered in cv_list (validator 0/0)?
   `--point` mode still emits the v1 point array_index.
6. VALIDATOR/SCOPE: bar is 0 errors on OUR output (not parity with the reference's 1 cv_list error) — stated?
   Both chunked + `--point` pass. CHUNK-01..06 all covered. No harness/process/phase comments. Reuse (no fork).

## Output

Prioritized findings: SEVERITY (BLOCKER/HIGH/MEDIUM/LOW/NIT), location, problem, concrete fix. End with one
line: VERDICT: SHIP / SHIP-WITH-FIXES / REWORK.
