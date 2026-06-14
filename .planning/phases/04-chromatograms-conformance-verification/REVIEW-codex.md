HIGH — `.planning/phases/04-chromatograms-conformance-verification/04-01-PLAN.md:218-222` — VER-04 L2 is vacuous: every finite `float` read from Parquet equals `(float)(double)itself`, so this would not catch a real intensity narrowing/write bug. Fix: compare against an independent expected stream at f32 width, or explicitly downgrade this from “round-trip” to a structural f32/finite check.

HIGH — `.planning/phases/04-chromatograms-conformance-verification/04-01-PLAN.md:154-160` — Reusing `BuildSelectedIonField()` and allowing a “minimal AUX struct” does not match ground truth. The existing selected_ion omits `ion_mobility_value`, `ion_mobility_type`, and `parameters`; ground truth also requires the full `AUX_ARRAY` shape. Fix: define the exact chromatogram metadata schema and emit those columns all-null/empty.

MEDIUM — `.planning/phases/04-chromatograms-conformance-verification/04-01-PLAN.md:137-143` — TIC fallback is underspecified and can dereference `trace[0]` when `trace.Length == 0`; the claimed “point count == records.Count” guard-path assertion is also false because fallback emits the same count. Fix: branch no-trace separately, re-key only when a trace exists, and assert a real fallback/1:1 signal.

MEDIUM — `.planning/phases/04-chromatograms-conformance-verification/04-01-PLAN.md:264-275` — VER-02 reads reference `spectra_peaks` but only requires `compared_spectra > 0` for reference `spectra_data`; this can pass with partial routed-set coverage and leaves centroid MS2 content unasserted. Fix: assert the exact compared reference-profile index set/count, and either compare reference centroid peaks to our peaks or explicitly scope them out.

LOW — `.planning/phases/04-chromatograms-conformance-verification/04-01-PLAN.md:147-168` — Chromatogram footer KV instructions drift from ground truth/current writer facts: `chromatograms_data.parquet` should not get `chromatogram_count`, and “spectra facet already writes 0” is only true for metadata, not data. Fix: specify exact data-vs-metadata footer keys.

VERDICT: SHIP-WITH-FIXES
