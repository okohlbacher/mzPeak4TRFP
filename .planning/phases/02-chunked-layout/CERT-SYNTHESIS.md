# v2 Phase 2 certification synthesis (close gate)

codex = CERTIFY-WITH-FIXES (4). vibe = unavailable (broken this session; codex carries).
72/72 tests; bitwise-lossless 48/48; validator 0/0 both modes; ~16% smaller.

| # | Sev | Finding (codex) | Fix |
|---|-----|-----------------|-----|
| C1 | HIGH | `MzPeakChunkCodec.Chunk` accepts non-finite/non-positive `width`; `width <= 0` infinite-loops (~:124); programmatic `ParseInput.MzPeakChunkSize` reaches it unchecked | Guard `width` to finite & > 0 in the codec/facet AND validate the `--chunk-size` CLI parse (reject ≤0/NaN with a clear error). Add a test. |
| C2 | HIGH | `MzPeakDifferentialTests` reference chunk decoder is NOT null-aware (`mz += d` throws on `None`); the reference fixture can have null-marked chunk rows | Make the test's reference decoder null-aware — mirror `MzPeakChunkCodec` decode (after a null, next value is absolute; skip null intensity points). |
| C3 | MED | The multi-row-group test forces point layout, so chunk list-column row-group flushing is NOT exercised | Add a chunk-specific lowered-cap test: force ≥2 row groups of chunk rows, decode ALL row groups, compare bitwise to the single-row-group chunked output. |
| C4 | NIT (constraint) | Forbidden test-hook/process comments in production code: MzPeakSpectrumWriter.cs:34, 156, 162 | Remove or rephrase without harness/test/hook wording; grep the Phase-2 diff for other leaks. |

All FIX NOW. C1 (DoS infinite loop) and C2 (latent decoder crash) are load-bearing. Re-verify: build,
full suite green native, mzpeak-validate 0/0 both modes, bitwise-lossless holds.
