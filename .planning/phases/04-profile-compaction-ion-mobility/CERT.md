# Phase 4 (rescoped: Ion Mobility / FAIMS) — Certification

**Scope delivered:** IM-01, IM-02. Profile compaction / null-marking (ZRS-01..04) dropped to backlog
BL-02 by decision.

## What shipped
- `scan.ion_mobility_value` populated from the Thermo FAIMS scan-trailer (`FAIMS Voltage On:` gate +
  `FAIMS CV:`), `ion_mobility_type` = CURIE **MS:1001581** (FAIMS compensation voltage). Mirrors the
  existing mzML writer semantics (MzMlSpectrumWriter.cs).
- Absent FAIMS → both columns null (unchanged for non-FAIMS files, verified on small.RAW: 48 rows all null).
- `selected_ion` ion-mobility has no Thermo source → stays null (IM-02).
- New `MzPeakColumns.AddNullableString` helper; static `MzPeakSpectrumWriter.ApplyIonMobility` seam.

## Verification
- Build: native arm64 (AnyCPU 8.0.37), 0 errors.
- Tests: **95/95** green (4 new synthetic-ScanTrailer ion-mobility unit tests: FAIMS on→CV+MS:1001581,
  FAIMS off→null, no trailer→null, on-without-CV→null).
- Conformance: `mzpeak-validate` **0 errors** in all 3 modes (default numpress, `--lossless`, `--point`).
  Only the validator's documented advisory `cv_term_placement_tables` warning remains (a known
  non-regressing limitation: mzML element-model MUSTs cannot map onto packed facets; validator ships it
  at WARNING). The chromatogram validator-gate test now tolerates the advisory ruleIds while still
  asserting 0 errors AND that no suppressed advisory finding references ion_mobility/FAIMS/MS:1001581.

## Adversarial review (codex; vibe non-functional all session)
- Verdict: **No CRITICAL/HIGH findings.** Confirmed: FAIMS gate/value/accession matches mzML; negative
  CVs accepted; selected_ion mobility null as intended; all-null `AddNullableString` byte-equivalent to
  `AddNullLeafScalar`; mixed null/non-null definition levels correct; `CollectPrefix(null)` sound.
- 1 MEDIUM (validator-gate suppression could mask a future real placement regression) — **fixed**: the
  test now fails if any suppressed advisory finding mentions an ion-mobility term.

## Commits
- feat(mzpeak): populate FAIMS ion mobility (IM-01/IM-02)
- test(mzpeak): harden validator gate against masked ion-mobility placement findings
