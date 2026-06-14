# v2 Phase 3 Research (consolidation)

Two parallel sub-reports (authoritative; read them):
- `RESEARCH-schema.md` — reference numpress schema, transform CURIEs, L2 bound, validator rules (pyarrow/pynumpress verified).
- `RESEARCH-port.md` — C# MSNumpress-linear port decision, algorithm spec, license, verification.

## Locked decision on the A1 conflict (intensity)

The reference `small.numpress.mzpeak` ALSO Numpress-SLOFs **intensity** (8th field
`intensity_numpress_slof_bytes`, transform MS:1002314, plain `intensity` dropped) — that's how it reaches
~63% smaller. **But the v2 milestone LOCKED "intensity stays lossless f32" (NP-04; out-of-scope table
explicitly excludes intensity SLOF).** → **DECISION: keep intensity lossless f32.**

Our Numpress mode therefore = **m/z Numpress-linear + plain f32 intensity list**:
- chunk struct: the Phase-2 fields, with `mz_chunk_values` **null** and `mz_numpress_linear_bytes:
  large_list<uint8>` populated; **`intensity` stays the plain `large_list<float>`** (NOT replaced by SLOF
  bytes; we do NOT emit `intensity_numpress_slof_bytes`).
- This is a VALID variant of the chunk schema (the format permits m/z-only numpress) — it is intentionally
  NOT byte/schema-identical to the reference. Reframe "schema diff vs reference" to: our m/z columns +
  transform match; intensity intentionally differs (plain f32). Reframe the size target: smaller than
  delta-chunked by the m/z saving only (NOT the full 63%, which required intensity SLOF).
- **Intensity-SLOF → backlog** (future v3 / opt-in), noted.

## Key facts to build on (from the sub-reports)

- **chunk_encoding (numpress m/z) = `MS:1002312`** (numpress-linear CURIE). array_index buffer_format
  `chunk_transform`, transform CURIE `MS:1002312` on the m/z column; `mz_chunk_values` present-but-null;
  `sorting_rank:0` retained. `chunk_transform` already in the array_index enum.
- **Fixed point: per-chunk optimal** (`optimalLinearFixedPoint`); **L2 m/z bound = 0.5/fp ≈ 4.6e-7 Th
  (~2.3e-9 relative)**, round-trip idempotent.
- **C# port: hand-port `MSNumpress.cs`** (~200 lines: OptimalLinearFixedPoint / EncodeLinear / DecodeLinear),
  Apache-2.0 (attribute ms-numpress in the header), pure managed AnyCPU (no x64 — must NOT reintroduce one).
  Verify against **pynumpress** (`python3.11 -import pynumpress`) + ms-numpress test vectors. Watch the
  pynumpress "phantom leading value" decode artifact (RESEARCH-port hand-off risk).
- **Validator: zero numpress-specific rules** — our output keeps 0 errors as long as we keep our existing
  exhaustive cv_list + index version (the reference file itself fails the same 2 errors we already fix).
- **Byte-identity with the reference m/z bytes** is achievable only with the same per-chunk optimal fixed
  point; it is NOT a gate — the gate is decode-within-L2-bound for m/z (intensity bit-exact f32).

## Open question for the planner

- CLI flag names: `--no-numpress` / `--lossless` (delta chunks), default numpress ON. Confirm naming +
  the lossy-m/z CLI warning + data_processing note text.
