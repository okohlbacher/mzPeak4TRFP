# Phase 3 plan review synthesis (plan gate)

codex = SHIP-WITH-FIXES; vibe = SHIP-WITH-FIXES. Raw: `REVIEW-codex.md`, `REVIEW-vibe.md`.
No REWORK, but several HIGH correctness items must be folded in before execution.

## Fixes to fold into 03-01-PLAN.md

| # | Sev | Finding | Resolution |
|---|-----|---------|------------|
| Q1 | HIGH | **Nested def/rep level computation is the crux and is under-specified** (codex CH3, vibe M1). Nested struct/list leaves need max-def-levels >1 (existing tests: `mz_range/lo`=2; list leaves 5/4/2). "def-level=1" is wrong for isolation_window / activation.parameters / scan_windows / padded tails / empty-vs-null lists | **Task 1 builds a GENERAL per-leaf level computer** covering: top-struct present/null, nested-struct present/null, null-list, empty-list, list-with-N-items, leaf-null. Prove it with pyarrow on the ACTUAL Phase-3 shapes (isolation_window struct, activation.parameters list, scan_windows list) — not just a toy. This helper is the foundation for Tasks 2-3 |
| Q2 | HIGH | **Precursor parent linkage** naive `lastMs1Ordinal` breaks filtered / MS2-only / ranged runs (codex CH2) | Maintain a `scanNumber → emitted ordinal` map; derive the precursor's parent scan via the mzML logic already in the codebase (`Master Scan Number` trailer / scan-string parent fallback — reuse MzMlSpectrumWriter/PrecursorInfo); map parent scan number → emitted ordinal. If the parent was filtered out / not emitted, define behavior explicitly (null `precursor_index`, keep precursor) and TEST the MS2-only path |
| Q3 | HIGH | **Counts are representation-specific** (codex CH1). Plan populates data_points always + 0 peaks | In our DUAL model: `number_of_data_points` = the spectrum's `spectra_data` point count; `number_of_peaks` = its `spectra_peaks` count when peaks were written for that ordinal, else NULL (def-level null, not 0). Assert on small.RAW: a profile+centroid scan has both populated; a scan with no peaks has `number_of_peaks` null |
| Q4 | HIGH | **Validator gate too weak** — only checks the scan-error string (codex CH4) | Parse validator output by rule/error id: assert `columns_spectra_metadata`(scan) AND `cv_list_declared` are ABSENT, and that no NEW ERROR id appears beyond an explicit allowlist (the pre-existing reference-archive failures). Make the gate decisive about "no new errors" |
| Q5 | MED | **List element must be named `item`** not `element` (codex CM1) — Arrow/Parquet.Net path is `…/list/item/…` | `BuildParamField("item")` for list elements; add a pyarrow schema-name assertion (`parameters/list/item/...`) |
| Q6 | MED | **cv_list drift guard** (codex CM2, vibe H1) | Generate `cv_list` by collecting CV prefixes from CvColumn usage + every PARAM `accession`/`unit` emitted; assert the index AND footer cv_list cover the set. Not hard-coded MS/UO |
| Q7 | DOC | **CONTEXT row-disjoint model contradicts RESEARCH** (vibe B2) + N-row schema ambiguity (vibe B1) + counts wording (vibe M2) | Add an explicit note at the top of PLAN: "Row model per RESEARCH.md Decision 1 (4 co-resident N-row columns, null-padded); CONTEXT.md's earlier row-disjoint wording is SUPERSEDED." State all four struct columns have N rows. Fix the `number_of_data_points , number_of_peaks` comma wording |

Q1–Q4 are load-bearing. After revision, recommend a quick codex confirm before execution (Task 1 level-builder
is the highest-risk; its pyarrow proof is the gate).
