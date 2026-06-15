# v2 Phase 4 plan review brief (plan gate)

Adversarial review of the PLAN. Read-only; findings only.

## Context
Phase 4 = profile compaction (zero-run strip + null-marking + per-spectrum δmz model) + FAIMS ion-mobility.
RESEARCH.md (empirically verified, 8-sig-fig δmz match) is authoritative. Null-marking ON by default for
PROFILE in numpress mode; --lossless/--point stay BITWISE-L1 (no marking); centroid spectra_peaks untouched.

## Read (repo root /Users/kohlbach/Claude/mzPeak4TRFR)
- .planning/phases/04-profile-compaction-ion-mobility/04-01-PLAN.md  ← THE PLAN
- .planning/phases/04-profile-compaction-ion-mobility/RESEARCH.md (authoritative algorithms)
- .planning/phases/04-profile-compaction-ion-mobility/CONTEXT.md
- Writer/MzPeak/MzPeakChunkCodec.cs, ChunkFacetStream.cs, ScanStager.cs, MzPeakMetadataFacetBuilder.cs, MzPeakSpectrumWriter.cs
- refs/mzPeak/small.{chunked,numpress}.mzpeak (pyarrow-verify)

## Evaluate
1. ALGORITHM CORRECTNESS: does the plan faithfully implement RESEARCH's skip-zero-runs + null-pair-mask + δmz WLS fit (weights sqrt(ln(I+1)), δ≤1.0 filter, order selection constant-iff-e_const<e_reg/10 else quadratic else None)? kept-count == number_of_data_points? Reconstruction (constant bit-exact, quadratic ≤~2e-5) decode path tested? Numpress composition (kept m/z incl flanking zeros in bytes; null markers as NULL intensity; mz_chunk_values null; mz_delta_model emitted) correct?
2. NO REGRESSION: is null-marking strictly gated to profile + numpress-default, so --lossless and --point stay BITWISE-L1 (no nulls, empty model)? Centroid spectra_peaks never touched? mzpeak-validate 0/0 all 3 modes asserted?
3. FIDELITY GUARANTEE: is "near-lossless" defined + asserted (kept non-zero points + peak apex exact; reconstructed nulled points within tolerance)? Any risk the strip drops a non-zero/peak point? Edge cases: <3 deltas, singular fit, all-zero spectrum, single peak.
4. IM/FAIMS: correct trailer gate (FAIMS Voltage On → FAIMS CV), MS:1001581, cv_list registration only when emitted, null when absent; tested via synthetic ScanTrailer since small.RAW has no FAIMS.
5. DECISIVENESS + runtime env in commands; reuse (no fork); flag naming; no process/phase comments.

## Output
Prioritized findings: SEVERITY (BLOCKER/HIGH/MEDIUM/LOW/NIT), location, problem, concrete fix. End with VERDICT: SHIP / SHIP-WITH-FIXES / REWORK.
