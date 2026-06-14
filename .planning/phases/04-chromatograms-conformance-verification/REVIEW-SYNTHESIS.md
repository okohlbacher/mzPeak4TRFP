# Phase 4 plan review synthesis (plan gate)

codex = SHIP-WITH-FIXES. vibe = (rerun in progress after a cost-cap abort; fold in before execution).
Raw: `REVIEW-codex.md`, `REVIEW-vibe.md`.

## Fixes to fold into 04-01-PLAN.md

| # | Sev | Finding (codex) | Resolution |
|---|-----|-----------------|------------|
| V1 | HIGH | **VER-04 L1/L2 is vacuous** — comparing a Parquet `float` to `(float)(double)itself` always passes; cannot catch a real intensity narrowing/write bug | Make the source truth INDEPENDENT of our written output: re-read small.RAW arrays in the test via the Thermo reader (a minimal dump) OR reuse the TRFP-mzML arrays already produced for VER-02; assert L1 = our `spectra_data.mz` (f64) bit-exact vs the independent source m/z, L2 = our intensity == `(float)` of the independent source intensity. If an independent read is truly impractical, DOWNGRADE VER-04 to an honest structural f32/f64-width + finiteness check and rely on VER-02 (independent reference) for value equality — do not call it a round-trip |
| V2 | HIGH | **Chromatogram metadata schema doesn't match ground truth** — blindly reusing `BuildSelectedIonField()` and a "minimal AUX struct" diverges (chromatogram selected_ion/precursor + full AUX_ARRAY shape) | Define the EXACT `chromatograms_metadata` schema from the ground-truth dump (chromatogram struct + present-but-null precursor/selected_ion + auxiliary_arrays full shape + number_of_auxiliary_arrays); emit the null/empty structs with the correct columns. Note: if our Phase-3 spectra `selected_ion` omits ion_mobility_value/type (OPT-04, deferred), keep facets CONSISTENT and document the v1 simplification rather than silently differing |
| V3 | MED | **TIC fallback underspecified** — can deref `trace[0]` when `trace.Length==0`; the "point count==records.Count" guard assertion is false because the fallback emits the same count | Branch the no-trace case separately (no `trace[0]` deref); re-key by scan number only when a trace exists; assert a REAL signal that distinguishes the device-trace path from the fallback path (e.g. compare a known device-TIC value vs summed-TIC for an MS1 row, which RESEARCH showed differ: 71263168 vs 15245068) |
| V4 | MED | **VER-02 coverage too weak** — only requires `compared_spectra > 0` for reference spectra_data; can pass with partial routed-set coverage; centroid MS2 unasserted | Assert the EXACT compared reference-profile index set/count (not just >0); and either compare reference centroid `spectra_peaks` to OUR `spectra_peaks` or explicitly scope centroid out with a stated reason |
| V5 | LOW | **Footer KV drift** — `chromatograms_data.parquet` should not get `chromatogram_count`; "spectra facet already writes 0" is true only for metadata, not data | Specify the exact data-vs-metadata footer keys per the ground-truth/current-writer facts; don't add count KV to the data facet |

V1 and V2 are load-bearing. After revision: fold any new vibe findings, then execute.
