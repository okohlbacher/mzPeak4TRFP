# Phase 4 certification synthesis (close gate — final)

codex = CERTIFY-WITH-FIXES. vibe = unavailable (repeated cost-cap aborts / hangs / exit 144 across this
session's Phase-4 gates; it succeeded at earlier gates — this is a tooling flakiness, not a skipped gate;
retried on the fixed code). Validator: PASS 0/0 with chromatograms; 51/51 tests; differential 11057==11057.

| # | Sev | Finding (codex) | Fix |
|---|-----|-----------------|-----|
| W1 | MED | `selected_ion` (writer ~810) omits `ion_mobility_value`, `ion_mobility_type`, `parameters` vs ground truth (emits 5 fields) | Add those columns as all-null / empty-list to match ground truth EXACTLY, on BOTH spectra and chromatogram selected_ion (and verify precursor shape too) so facets are consistent and truly ground-truth-shaped. (ion_mobility VALUES remain deferred = OPT-04, but the COLUMNS should exist.) |
| W2 | MED | L2 value-equality over-attributed to VER-02, which only compares the 14 reference-profile spectra (centroid/MS2 scoped out) | Make the L2 claim honest: qualify it precisely to the 14 profile spectra, AND add a lightweight INDEPENDENT intensity check for the remaining emitted spectra (e.g. compare our intensities to the TRFP-mzML arrays already produced for VER-02) — or, if impractical, explicitly state L2 is structural-only for non-profile spectra. No overclaim |
| W3 | LOW | `cv_list` finalized in `BuildMetadataFacet` BEFORE chromatogram builders collect their CURIE prefixes → the "exhaustive coverage via CollectPrefix" invariant holds only accidentally (all chromatogram prefixes happen to be MS/UO) | Collect chromatogram CURIE prefixes BEFORE `AddMetadataBlocks`, or finalize the metadata blocks after ALL facets (incl chromatograms) are built, so cv_list is genuinely exhaustive |
| W4 | NIT (constraint!) | Forbidden phase/process comments remain: `VER-02`/`VER-03` at MzPeakWriterTests.cs:1103, 1183, 1201 | Rephrase as domain rationale without phase IDs; then grep the whole Phase-4 diff for any other `VER-0\|phase\|harness\|gsd\|spike` comment leaks and fix all |

All FIX NOW (small). Re-verify: build, 51+ tests green, `mzpeak-validate` still PASS 0/0, differential still green.
W4 is a hard project constraint (no process comments in code).

## vibe outage note

The Phase-4 plan-gate and cert-gate `vibe` runs repeatedly cost-capped, hung (>60 min, no output), or exited 144 this session — a tooling flakiness specific to today (vibe reviewed Phases 1-3 plan+cert gates successfully). codex provided thorough adversarial review at BOTH Phase-4 gates (plan: SHIP-WITH-FIXES, 5 findings; cert: CERTIFY-WITH-FIXES, 4 findings), all addressed. Dual-review intent honored to the extent the tool allowed; vibe unavailability documented rather than silently skipped.
