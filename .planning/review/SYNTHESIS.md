# Adversarial code review — synthesis & disposition

Sources: `REVIEW-codex.md` (correctness/mappings/completeness, pyarrow-verified) + internal code-reviewer
(SoC/maintainability/tests) + my schema-diff of our output vs `small.numpress.mzpeak`.

## A. FIX — correctness / completeness (genuine gaps)

| # | Finding | Fix | Evidence |
|---|---------|-----|----------|
| A1 | **Nullability bug**: unknown polarity→+1, missing ion-injection-time→0, missing base-peak→0, MSn w/o reaction→0 isolation/selected_ion. Asserts FALSE data. | Make the `Record` fields nullable; emit leaf-null (not 0) when the value is genuinely absent. | codex HIGH ×2 |
| A2 | **`spectrum` missing 4 ref columns**: `data_processing_ref`, `auxiliary_arrays`, `number_of_auxiliary_arrays`, `mz_delta_model` | Emit them to match the reference schema: data_processing_ref null, auxiliary_arrays empty list, number_of_auxiliary_arrays 0, mz_delta_model null (Phase-4 null-marking placeholder). | schema-diff: ours 15 vs ref 19 |
| A3 | **`scan` missing 5 ref columns**: `MS_1000616_preset_scan_configuration`, `ion_mobility_value`, `ion_mobility_type`, `spectrum_reference`, `parameters` | Emit them: preset_scan_config from Thermo if available else null; ion_mobility_value/type null (Phase-4 FAIMS); spectrum_reference null; parameters empty list. | schema-diff: ours 7 vs ref 12 |
| A4 | **Wrong provenance**: data_processing records `MS:1000544` "Conversion to mzML" — false for RAW→mzPeak | Use a correct/generic term (e.g. MS:1000530 "file format conversion") + the numpress/sort transform IDs. | codex MEDIUM |
| A5 | **Empty spectra dropped entirely** (loses the scan's metadata/RT/precursor) | Emit a metadata `spectrum` row with number_of_data_points=0 (and number_of_peaks null/0), NO data-facet rows. RELAX the parity invariant: data spectrum_index set ⊆ metadata spectrum set (was ==). Update parity tests. | codex HIGH; reference keeps empty spectra |
| A6 | point `spectrum_array_index` lacks per-facet transform/data_processing entries; peaks shouldn't blindly share the spectra_data index | Split array-index constants per facet (data vs peaks). | codex MEDIUM |

## B. DOCUMENT AS DEVIATION — not bugs (scope-locked or infeasible)

- **Intensity SLOF** (`intensity_numpress_slof_bytes`, MS:1002314): the reference SLOFs intensity; we keep
  lossless f32 by LOCKED milestone decision. NOT a fix — document the deliberate deviation in code/README.
- **Arrow field metadata** on signal columns (array_accession/transform/unit/…): the reference attaches
  per-field Arrow metadata; Parquet.Net does not emit ARROW:schema field metadata. We convey the same info
  via the Parquet footer `spectrum_array_index` KV (which `mzpeak-validate` reads). VERIFY Parquet.Net truly
  can't; if not, document the equivalent-via-footer approach. (codex HIGH — reclassify to documented-limitation if infeasible.)
- **large_string/large_list**: our output uses string/list; passes pyarrow + validator. Document accepted compatibility.
- **Chromatogram chunking** (numpress ref chunks chromatograms): deferred to Phase 5; keep point + document.

## C. CLEANUP — maintainability/bloat (internal review)

- C1 Dead code: `MzPeakParquet.PresentLevel`, `LeafNullLevel`, `NullListAt`, the unused `MzPeakParam` struct; verify/remove unused `ChunkStructField()` overload.
- C2 Dedupe: collapse `AddMsnNullable{Float,Double,Int}` → one generic; `AddEmptyList`==`AddEmptyAuxList` (delete one).
- C3 Centralize CURIEs into a `Cv.*` constants table (remove triple-listing writer + `ChromDataAccessions` + tests).
- C4 Strip process/phase/cert/research comments (the internal review's §4 + codex LOW list); rename `V1*` test constants → `Baseline*`.
- C5 Shared `MzPeakTestSupport` (ResolvePython/Validator/ReadEntry/Leaf/PyArrow + the delta-decode duplicated 5×).

## D. TEST COVERAGE — close gaps

- D1 Run schema/value gates over ALL THREE modes (point / lossless-chunk / numpress-default) — most tests force `--point`.
- D2 Summed-TIC fallback branch (untested); empty-input "No in-range spectrum" throw (untested deterministically); charge-state-present path (small.RAW has none).
- D3 Add a pyarrow schema-parity gate: our metadata `spectrum`/`scan` columns == reference column set (would have caught A2/A3).
- D4 Delete tautological tests (the prefix-array self-equality).

## E. SEPARATION OF CONCERNS — decompose the 2006-line writer (LOW prio, do last)

Per internal review, ordered move-method steps (each test-gated): (1) `MzPeakLayout` constants → (2) promote
the 3 facet streams → (3) `MzPeakColumns` helper family → (4) `MzPeakMetadataFacetBuilder` + `ChromatogramBuilder`
→ (5) `MzPeakIndexBuilder`/metadata-blocks → (6) `ScanStager`/`Record` → (7) explicit `CvCollector` (remove `Cv()` side effect).

## Execution order
Wave 1 = A (correctness/completeness) + the schema-parity gate D3. Wave 2 = C (cleanup/bloat) + D (coverage).
Wave 3 = E (SoC decomposition). Verify after each: build, full suite green native arm64, mzpeak-validate 0/0
(point/lossless/numpress), schema-diff cols == reference. Re-review (codex) at the end.
