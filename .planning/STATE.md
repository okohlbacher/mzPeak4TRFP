---
gsd_state_version: 1.0
milestone: v2
milestone_name: compression-fidelity-scale
status: in-progress
last_updated: "2026-06-14T18:31:44.044Z"
last_activity: 2026-06-14 — Phase 2 (Chunked Layout) executed: chunked spectra_data is the new default, m/z delta bit-exact, 0 validator errors both modes.
progress:
  total_phases: 5
  completed_phases: 2
  total_plans: 2
  completed_plans: 2
  percent: 40
---

# Project State

## Project Reference

See: .planning/PROJECT.md (updated 2026-06-14)

**Core value:** Smaller, reference-structured mzPeak output (chunked + Numpress) that scales to multi-GB RAW and is robust to imperfect scans — without regressing v1 conformance.
**Current focus:** v2 milestone — Phase 2 (Chunked Layout)

## Current Position

Milestone: v2 ("compression, fidelity & scale") — 5 phases, sequential
Phase: 2 of 5 (Chunked Layout) — COMPLETE; ready to plan Phase 3 (Numpress-Linear m/z)
Status: Phase 2 executed (02-01-PLAN.md → 02-01-SUMMARY.md); chunked default, CHUNK-01..06 done
Last activity: 2026-06-14 — Phase 2 chunked layout shipped; full suite 72/72 green native arm64.

Progress: [████░░░░░░] 40% (v2)

## Milestone history

- **v1 — point-layout writer** ✓ Certified 2026-06-14 (Phases 1–4). Native arm64 (8.0.37) added post-v1. Archived under `.planning/archive/v1-point-layout/`.

## Accumulated Context

### Decisions (see PROJECT.md Key Decisions)

- v2 default = chunked layout + Numpress-linear m/z (lossy m/z, recorded); `--lossless`/`--point` opt-outs.
- v2 = format features + operational (streaming for multi-GB, per-scan robustness).
- Build/runtime: AnyCPU, native arm64 via RawFileReader 8.0.37; `DOTNET_ROLL_FORWARD` for net8→net9/10; no Rosetta/mzLib.
- Phase 2: chunked spectra_data (6-field reference struct, chunk_encoding=MS:1003089) is the new default; `--point` restores v1; `--chunk-size` configures the window (default 50.0).
- Phase 2: m/z f64 delta encode+reconstruct is BIT-EXACT on real Thermo m/z (48/48 spectra, 0 mismatches) → losslessness is exact/L1, no tolerance. Intensity bit-exact by construction.
- Phase 2: `chromatograms_data` stays POINT as a deliberate documented deviation; the reference CHUNKS it → full chromatogram chunking deferred to Phase 5.
- Phase 3 input: chunk struct stays 6 fields in delta mode; `mz_numpress_linear_bytes` (7th field) is added ONLY for the Numpress encoding, not present-but-empty.

### Key Artifacts

- `refs/_findings/mzpeak_groundtruth_schema.md` — chunk schema (mz_chunk_start/end, mz_chunk_values, chunk_encoding, mz_numpress_linear_bytes).
- `tools/e2e/` — corpus comparison harness (97 RAW↔mzpeak pairs) for VER2 re-verification.
- `~/Claude/mzPeakValidator`, `~/Claude/mzML2mzPeak` — conformance oracle + differential reference.

### Backlog

- BL-01: richer mzLib MGF validation (mzLib x64-only) — `.planning/BACKLOG.md`.
- Deferred debug: E2E `DIFF`/`COMPARE_ERROR`/`CONVERT_FAIL` findings from the v1 sweep (ROB-01/VER2 address some).

---
*Last updated: 2026-06-14 after v2 milestone planning*
