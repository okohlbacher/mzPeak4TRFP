# Phase 2 plan review synthesis (plan gate)

codex = SHIP-WITH-FIXES; vibe = REWORK. Raw: `REVIEW-codex.md`, `REVIEW-vibe.md`.

## Decisions + fixes to fold into 02-01-PLAN.md (and CONTEXT.md)

| # | Sev | Finding (source) | Resolution |
|---|-----|------------------|------------|
| P1 | BLOCKER | Plan keeps Phase-1 single-row `spectra_metadata` while `spectra_data` gains many spectra → index/count inconsistency; invalidates the validator claim (codex) | **Emit one `spectrum` metadata row per output ordinal NOW** (index 0..N-1, id, time; `spectrum_count`=N). Rich fields + scan/precursor/selected_ion facets stay Phase 3. Keep data/metadata spectrum sets identical |
| P2 | BLOCKER/HIGH | **Centroid routing** — single-representation vs dual (vibe BLOCKER, codex HIGH) | **Adopt DUAL emission, matching the reference + leveraging Thermo label peaks:** `spectra_data` ← as-acquired arrays via `ReadMZData(centroid=false)` for every in-range scan; `spectra_peaks` ← Thermo `CentroidStream` via `ReadMZData(centroid=true)` ONLY when `scanEvent.ScanData==Profile && scan.HasCentroidStream` (profile scan that also has label peaks). Centroid-only scans → `spectra_data` only (no duplicate). Update CONTEXT to match. VER-02 facet-equivalence specifics deferred to Phase 4 (differential will run TRFP mzML in profile mode to align) — do NOT over-claim equivalence in Phase 2 |
| P3 | HIGH | index `data_kind:"peak arrays"` is wrong; readers expect `"peaks"` (codex) | Use `{name:"spectra_peaks.parquet", entity_type:"spectrum", data_kind:"peaks"}`; assert in test |
| P4 | HIGH | `SpectrumArrayIndex` const lacks `sorting_rank:0` on `point.mz` (vibe) | Add `sorting_rank:0` to the `point.mz` entry in the const |
| P5 | MED | Stable-sort claim is false: `ReadMZData` already ran an unstable `Array.Sort`, so original RAW tie order is gone (codex) | **Drop the resort.** Thermo SegmentedScan/CentroidStream are already m/z-ascending; emit in that order, declare `sorting_rank:0`, and ASSERT non-decreasing m/z + multiset preserved (count in == count out). Only fall back to a paired stable sort if an ascending-violation is ever detected. Weaken the claim to "non-decreasing m/z + multiset preserved" |
| P6 | MED | Acceptance not decisive (codex, vibe) | Gates must: run `mzpeak-validate`; assert `spectrum_count == distinct spectrum_index count` in BOTH facets; assert peaks metadata point-count; assert index `data_kind:"peaks"`; add a synthetic duplicate-m/z unit test (not tautological) |
| P7 | MED | Memory strategy undecided; helper writes one row group only (codex, vibe) | **Explicit v1 deferral:** full in-memory accumulation + single row group is the documented prototype approach; remove any "large-RAW safe" claim; note row-group batching as a v2/OPT item |
| P8 | HIGH | Verify command missing `DOTNET_ROOT_X64` (vibe) | Add `DOTNET_ROOT_X64=$HOME/.dotnet-x64` (+ roll-forward) inside every run/test verify command |

P1, P2, P3 are the load-bearing changes. Note: vibe's "loop only processes first spectrum" BLOCKER is the
Phase-1 placeholder that Task 1 already replaces — not a plan defect, but confirms Task 1 must fully remove
the `masses==null` single-shot guard.
