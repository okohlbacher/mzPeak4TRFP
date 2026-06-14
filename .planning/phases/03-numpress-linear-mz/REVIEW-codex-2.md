Re-review result: N1-N3 are resolved.

- N1: RESOLVED. Task 1 now explicitly rejects the bad prose and requires canonical source-port behavior: max-value `optimalLinearFixedPoint`, 4-byte LE first values, and canonical half-byte `encodeInt/decodeInt` at [03-01-PLAN.md:145](/Users/kohlbach/Claude/mzPeak4TRFR/.planning/phases/03-numpress-linear-mz/03-01-PLAN.md:145), [03-01-PLAN.md:152](/Users/kohlbach/Claude/mzPeak4TRFR/.planning/phases/03-numpress-linear-mz/03-01-PLAN.md:152), [03-01-PLAN.md:156](/Users/kohlbach/Claude/mzPeak4TRFR/.planning/phases/03-numpress-linear-mz/03-01-PLAN.md:156), [03-01-PLAN.md:162](/Users/kohlbach/Claude/mzPeak4TRFR/.planning/phases/03-numpress-linear-mz/03-01-PLAN.md:162). Checked against official ms-numpress C++ source and `pynumpress`.
- N2: RESOLVED. Numpress mode now mandates `MzPeakParquet.NullList`, explicitly not `EmptyList`, and Task 4 tests `null_count == row count` with zero present/empty lists at [03-01-PLAN.md:225](/Users/kohlbach/Claude/mzPeak4TRFR/.planning/phases/03-numpress-linear-mz/03-01-PLAN.md:225) and [03-01-PLAN.md:302](/Users/kohlbach/Claude/mzPeak4TRFR/.planning/phases/03-numpress-linear-mz/03-01-PLAN.md:302).
- N3: RESOLVED. The decode helper is guarded for short chunks at [03-01-PLAN.md:235](/Users/kohlbach/Claude/mzPeak4TRFR/.planning/phases/03-numpress-linear-mz/03-01-PLAN.md:235), and Task 4 now requires chunk count, finite fp, per-chunk length parity, total count parity, start/end anchors, and max error at [03-01-PLAN.md:286](/Users/kohlbach/Claude/mzPeak4TRFR/.planning/phases/03-numpress-linear-mz/03-01-PLAN.md:286).

Vector checks with `python3.11` + `pynumpress 0.0.9`:
- `optimal_linear_fixed_point([1,2,3]) == 1073741823.0`, so plan value is correct.
- V2 bytes for `[100.0,100.5,101.0,101.5]`, `fp=21367996`: `41 74 60 CB C0 00 00 00 70 F9 5C 7F CE FF FF 7F 88`; prefix matches the plan and decode is exact.
- Reference row 0 prefix: `41 64 37 6C 40 00 00 00` -> `10599266.0`; `chunk_encoding == MS:1002312`; `mz_chunk_values is None`; start-anchor error `2.82e-08 <= 4.72e-08`.

NEW issues:
- Minor test-oracle caveat: `pynumpress.decode_linear` rejects 12-byte single-value streams in this install, despite canonical source behavior being `dataSize==12 -> 1 value`. Do not use `pynumpress` as the oracle for the single-value edge test.
- Minor plan hardening: paste the full V2 byte vector above into the plan so “known reference vector” cannot become circular via `pynumpress.encode_linear`.

VERDICT: SHIP-WITH-FIXES
