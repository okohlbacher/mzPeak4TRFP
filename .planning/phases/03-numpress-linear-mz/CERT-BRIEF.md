# v2 Phase 3 certification review brief (close gate)

Adversarial review of the IMPLEMENTED code. Read-only; findings only.

## What shipped

C# MSNumpress-linear port (`Writer/MzPeak/MSNumpress.cs`, Apache-2.0); numpress is the new DEFAULT m/z
encoding — m/z → `mz_numpress_linear_bytes`, `chunk_encoding=MS:1002312`, `mz_chunk_values` NULL, intensity
stays plain f32 (locked m/z-only scope). `--no-numpress`/`--lossless` → delta (L1); `--point` → v1.
Byte-identical to pynumpress; validator 0/0 in all 3 modes; L2 worst |Δm/z|≈1.6e-7; ~19% smaller than delta.
86/86 tests. A uint32-wraparound bug was found+fixed (int64 accumulation, bytes unchanged).

## Read (repo root /Users/kohlbach/Claude/mzPeak4TRFR)

- ThermoRawFileParser/Writer/MzPeak/MSNumpress.cs (the port) + the chunk codec
- ThermoRawFileParser/Writer/MzPeakSpectrumWriter.cs (numpress branch, array_index, data_processing)
- ThermoRawFileParser/ParseInput.cs, MainClass.cs (flags/default/warning)
- ThermoRawFileParser/ThermoRawFileParserTest/ (numpress + L2 + 3-mode tests)
- .planning/phases/03-numpress-linear-mz/03-01-PLAN.md, RESEARCH*.md, 03-01-SUMMARY.md
- refs/mzPeak/small.numpress.mzpeak (reference m/z columns)

## Evaluate

1. PORT CORRECTNESS: is MSNumpress.cs a faithful canonical port (OptimalLinearFixedPoint max-value;
   encodeInt/decodeInt half-byte; the uint32→int64 accumulation fix correct AND still byte-identical to
   pynumpress)? Does it handle edge cases (0/1/2 values, empty, large m*fp)? Decode round-trips within 0.5/fp?
   Apache-2.0 attribution present? AnyCPU (no x64) preserved?
2. SCHEMA: numpress chunk row has `mz_chunk_values` NULL (not empty — null_count==rows), bytes populated,
   intensity plain f32; chunk_encoding=MS:1002312; array_index records MS:1002312 (chunk_transform) +
   sorting_rank:0; cv_list includes MS:1002312 (exhaustive). data_processing records the lossy transform.
3. L2 TEST RIGOR: anti-vacuity guards present (chunk>0, fp finite>0, per-chunk decoded len==intensity len,
   total compared==spectrum_data_point_count, 1/2-value guards, BOTH anchors)? Could it pass vacuously?
4. NO REGRESSION: `--lossless` and `--point` remain BITWISE-L1 (Phase-1/2 gates intact); flag precedence
   (point > lossless > numpress-default) correct; CLI warning only in numpress mode; mzpeak-validate 0/0 ×3.
5. STREAMING: numpress encodes per-chunk within the streaming/row-group path (constant memory preserved);
   per-scan robustness intact.
6. BLOAT/STYLE: dead code, duplication vs reuse of codec/handle/NullList, redundant comments, any
   harness/process/phase comment (forbidden) — exact lines; BOM on new files; the vendored MSNumpress.cs
   license header.

## Output

Prioritized findings: SEVERITY (BLOCKER/HIGH/MEDIUM/LOW/NIT), file:line, problem, concrete fix. End with one
line: VERDICT: CERTIFY / CERTIFY-WITH-FIXES / REWORK.
