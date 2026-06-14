# v2 Phase 2 certification review brief (close gate)

Adversarial review of the IMPLEMENTED code. Read-only; findings only.

## What shipped

`spectra_data` now emits the reference 6-field CHUNK struct (delta-encoded m/z, chunk_encoding=MS:1003089,
transforms MS:1003901/02) via the Phase-1 streaming handle; chunked is the new DEFAULT, `--point` restores
v1; `--chunk-size` (default 50). `spectra_peaks`/`chromatograms_data` stay point. Bitwise-lossless vs v1
point (48/48 spectra, 0 mismatches); validator 0/0 both modes; ~16% smaller. 72/72 tests native arm64.

## Read (repo root /Users/kohlbach/Claude/mzPeak4TRFR)

- ThermoRawFileParser/Writer/MzPeak/  (the chunk codec — MzPeakChunkCodec or similar) + MzPeakParquet.cs
- ThermoRawFileParser/Writer/MzPeakSpectrumWriter.cs (ChunkFacetStream, --point/--chunk-size, array_index)
- ThermoRawFileParser/ParseInput.cs, MainClass.cs (flags)
- ThermoRawFileParser/ThermoRawFileParserTest/  (chunk tests + MzPeakDifferentialTests)
- .planning/phases/02-chunked-layout/02-01-PLAN.md, RESEARCH.md, 02-01-SUMMARY.md
- refs/mzPeak/small.chunked.mzpeak (reference; pyarrow-dump to verify)

## Evaluate

1. CODEC CORRECTNESS: delta encode/decode bit-exact and invertible for all inputs (duplicate/all-equal m/z,
   single-point spectrum = one length-1 chunk, empty spectrum = no row, non-monotonic guard). Does the
   DECODER correctly handle reference null markers ([None, absolute, delta], [None, None]) for reading
   reference / future Phase-4 files? Window/boundary-singleton logic correct (no dropped/duplicated points)?
2. LIST-COLUMN STREAMING: rep/def levels for list<double>/list<float> correct under row-group flushing; a
   chunk row's lists never split across row groups; reuse of the Phase-1 handle (no fork); temp-file path intact.
3. SCHEMA/FOOTER FIDELITY: 6-field struct names/types == reference; chunk_encoding + array_index transforms +
   sorting_rank match small.chunked.mzpeak verbatim; cv_list exhaustive (every new CURIE registered); --point
   retains the v1 point array_index.
4. NO v1 REGRESSION: bitwise multiset == v1 point per spectrum (m/z AND intensity); facet parity; mzpeak-validate
   0/0 in BOTH modes; peaks/chromatograms unchanged.
5. DIFFERENTIAL TEST (important): `MzPeakDifferentialTests` decodes the TRFP side as POINT (`r['point']`).
   Now the TRFP default is CHUNKED — does the test convert the TRFP side with `--point`, or decode chunk, or
   is it silently broken/passing vacuously? Verify it still meaningfully compares (and isn't KeyError-skipped).
6. CHROMATOGRAM DEVIATION: chromatograms_data point is documented as a deliberate Phase-2 deviation (reference
   chunks it) — is that stated honestly in code/SUMMARY, not as "reference keeps it point"?
7. BLOAT/STYLE: dead code, duplication, redundant comments, any harness/process/phase comment (forbidden) —
   exact lines; BOM on new files. Reuse of MzPeakParquet (extend, not fork).

## Output

Prioritized findings: SEVERITY (BLOCKER/HIGH/MEDIUM/LOW/NIT), file:line, problem, concrete fix. End with one
line: VERDICT: CERTIFY / CERTIFY-WITH-FIXES / REWORK.
