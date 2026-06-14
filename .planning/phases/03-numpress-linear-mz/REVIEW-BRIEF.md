# v2 Phase 3 plan review brief (plan gate)

Adversarial review of the PLAN. Read-only; findings only.

## Context

Phase 3 = Numpress-linear m/z (new default; `--no-numpress`/`--lossless` → Phase-2 delta; `--point` → v1).
LOCKED scope: intensity stays lossless f32 (m/z-only numpress; NOT byte-identical to the reference which
also SLOFs intensity). C# MSNumpress hand-port. RESEARCH (RESEARCH.md + RESEARCH-schema.md + RESEARCH-port.md,
pyarrow/pynumpress-verified): chunk_encoding/transform = MS:1002312, per-chunk optimal fixed point,
L2 bound 0.5/fp ≈ 4.6e-7 Th, mz_chunk_values null when numpress, intensity stays plain f32 list.

## Read (repo root /Users/kohlbach/Claude/mzPeak4TRFR)

- .planning/phases/03-numpress-linear-mz/03-01-PLAN.md  ← THE PLAN
- .planning/phases/03-numpress-linear-mz/RESEARCH.md (locked A1 decision), RESEARCH-schema.md, RESEARCH-port.md
- refs/mzPeak/small.numpress.mzpeak (reference; pyarrow-dump to verify the m/z columns + transform)
- ThermoRawFileParser/Writer/MzPeak/ (Phase-2 chunk codec), MzPeakSpectrumWriter.cs, ParseInput.cs, MainClass.cs

## Evaluate

1. NUMPRESS PORT CORRECTNESS: is the MSNumpress-linear algorithm spec in the plan precise enough to implement
   correctly (8-byte fixed-point header, first two values as 5-byte ints, 4-bit half-byte residual packing,
   optimalLinearFixedPoint)? Are the tests decisive — known ms-numpress test vectors + pynumpress cross-check
   + round-trip ≤ 0.5/fp? Is the "phantom leading value" pynumpress decode artifact handled so the L2 test
   isn't comparing misaligned arrays? Is the AnyCPU/no-x64 assertion real?
2. L2 vs L1 INTEGRITY: numpress mode = bounded L2 (m/z), intensity bit-exact f32. Do `--lossless` and
   `--point` remain BITWISE-L1 (Phase-1/2 gates untouched)? Is the L2 bound asserted correctly (max|Δm/z| ≤
   0.5/fp using the SAME per-chunk fixed point the encoder used)? Could the bound test pass vacuously?
3. SCHEMA/SCOPE HONESTY: m/z-only numpress (mz_chunk_values null, mz_numpress_linear_bytes populated, intensity
   plain f32). Is the plan honest that this is NOT byte/schema-identical to the reference (which SLOFs
   intensity) and that the size win is m/z-only (not 63%)? Is intensity-SLOF correctly deferred (not silently
   implemented or claimed)? Is the chunk-encoding branch INSIDE the codec (not a forked facet)?
4. CONFORMANCE: transform CURIE MS:1002312 recorded in array_index (chunk_transform) AND data_processing;
   cv_list stays exhaustive (MS:1002312 registered) → mzpeak-validate 0/0 in ALL THREE modes. Is the validator
   gate asserted per-mode by error id? Verify MS:1002312 is correct (the ROADMAP sketch wrongly said MS:1003089
   = delta) against the reference file.
5. ACCEPTANCE DECISIVENESS + runtime env (native arm64, DOTNET_ROLL_FORWARD, bin/Release, python3.11) in commands;
   reuse of Phase-2 codec + Phase-1 handle (no fork); no harness/process/phase comments; Apache-2.0 attribution
   for the vendored port.

## Output

Prioritized findings: SEVERITY (BLOCKER/HIGH/MEDIUM/LOW/NIT), location, problem, concrete fix. End with one
line: VERDICT: SHIP / SHIP-WITH-FIXES / REWORK.
