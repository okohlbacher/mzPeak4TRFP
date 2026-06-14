# v2 Phase 3 plan review synthesis (plan gate)

codex = REWORK (1 BLOCKER, 1 HIGH, 1 MEDIUM). vibe = unavailable (broken this session).

| # | Sev | Finding (codex) | Resolution |
|---|-----|-----------------|------------|
| N1 | BLOCKER | The MSNumpress spec is wrong: `optimalLinearFixedPoint` described as least-squares slope (canonical is max-value based), residuals described as continuation-varint nibbles (canonical is the specific `encodeInt`/`decodeInt` half-byte scheme). Implementing from this prose → broken numpress | Rewrite Task 1 to require a FAITHFUL line-for-line port of the canonical ms-numpress `optimalLinearFixedPoint` + `encodeInt`/`decodeInt` + `encodeLinear`/`decodeLinear` (port from the C++/Java reference, NOT the prose). Remove the incorrect least-squares/varint description. Mandate exact verification: `optimalLinearFixedPoint([1,2,3]) == 1073741823`; reference row-0 fixed point `== 10599266.0`; BYTE-IDENTITY against a known ms-numpress test vector AND against pynumpress encode at the same fixed point (not just decode-within-bound). |
| N2 | HIGH | Plan conflates null vs empty `mz_chunk_values`: the reference column is ALL-NULL, but `MzPeakParquet.EmptyList` writes present zero-length lists | In numpress mode emit `mz_chunk_values` as NULL via `MzPeakParquet.NullList(list)` (parent-present / list-null), NOT EmptyList. Test: `mz_chunk_values` null_count == row count, zero present/empty lists. |
| N3 | MED | L2-bound test is vacuity-prone: a bad decode/drop could compare fewer/zero values; the phantom-leading-value drop helper indexes `decoded[1]` on short chunks | Add anti-vacuity guards: assert chunk count > 0; fp finite & > 0; per-chunk decoded length == intensity (== lossless) length; total compared count == `spectrum_data_point_count`; guard 1- and 2-value chunks; validate BOTH the start and end anchor m/z. |

All FIX NOW. N1 (correct canonical port + exact vectors) is load-bearing. After revision, recommend a codex
confirm of the algorithm-spec + vectors before execution.
