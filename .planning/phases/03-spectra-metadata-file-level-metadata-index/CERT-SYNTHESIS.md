# Phase 3 certification synthesis (close gate)

vibe = CERTIFY (no findings). codex = CERTIFY-WITH-FIXES. Validator: PASS (0 errors, 0 warnings); 44/44 tests.

| # | Sev | Finding (codex) | Fix |
|---|-----|-----------------|-----|
| H1 | HIGH | `MzPeakParquet.NullList()` always yields def-level 0 → cannot represent a null list whose parent structs ARE present (encodes path as root-absent instead). Latent: current data always writes activation.parameters, but it's a reusable primitive | Make `NullList` level-aware from the list's ancestor chain (def = the "parent present, list null" level), reserving def=0 only for top-level absence. Add a pyarrow assertion distinguishing `activation != null && parameters == null` from `activation == null` |
| M1 | MED | Linkage test only checks `precursor.source_index` sorted, not == exact MSn ordinal set; doesn't check `selected_ion.precursor_index == precursor.precursor_index` | Compute msnOrdinals from `spectrum.ms_level>=2`; assert `srcs == msnOrdinals`; assert both selected_ion linkage fields mirror precursor per row |
| L1 | LOW | cv_list test asserts only a superset → hard-coded extras / missing PARAM-only/footer-only prefixes would pass | Recursively collect accession+unit prefixes from metadata JSON/footer + PARAM lists; assert EXACT equality for index and footer cv_list IDs |
| L2 | LOW | Dead/unused code: `msnAtRow` (writer:420), unused `AddScalar` arg (:582), unused `present` arg (:787), unused `enc` (:860) | Remove to keep the writer tight |
| N1 | NIT (but constraint!) | Process/phase reference in a comment ("Phase-3 facet shapes") at MzPeakParquetTests.cs:267 — violates the no-process-comments rule | Replace with domain wording ("real mzPeak spectra-metadata facet shapes") |

All FIX NOW (small). Re-verify: build, 44+ tests green, `mzpeak-validate` still PASS (0/0).
Note N1: also grep the whole Phase-3 diff for any other harness/process/phase comment leaks.
