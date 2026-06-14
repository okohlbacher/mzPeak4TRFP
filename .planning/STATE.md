---
gsd_state_version: 1.0
milestone: v2
milestone_name: compression-fidelity-scale
status: planned
last_updated: "2026-06-14T16:40:00.000Z"
last_activity: 2026-06-14
progress:
  total_phases: 5
  completed_phases: 1
  total_plans: 0
  completed_plans: 0
  percent: 0
---

# Project State

## Project Reference

See: .planning/PROJECT.md (updated 2026-06-14)

**Core value:** Smaller, reference-structured mzPeak output (chunked + Numpress) that scales to multi-GB RAW and is robust to imperfect scans — without regressing v1 conformance.
**Current focus:** v2 milestone — Phase 2 (Chunked Layout)

## Current Position

Milestone: v2 ("compression, fidelity & scale") — 5 phases, sequential
Phase: 2 of 5 (Chunked Layout)
Status: Milestone planned (PROJECT/REQUIREMENTS/ROADMAP); ready to plan Phase 1
Last activity: 2026-06-14 — v1 archived to `.planning/archive/v1-point-layout/`; v2 milestone defined.

Progress: [░░░░░░░░░░] 0% (v2)

## Milestone history

- **v1 — point-layout writer** ✓ Certified 2026-06-14 (Phases 1–4). Native arm64 (8.0.37) added post-v1. Archived under `.planning/archive/v1-point-layout/`.

## Accumulated Context

### Decisions (see PROJECT.md Key Decisions)

- v2 default = chunked layout + Numpress-linear m/z (lossy m/z, recorded); `--lossless`/`--point` opt-outs.
- v2 = format features + operational (streaming for multi-GB, per-scan robustness).
- Build/runtime: AnyCPU, native arm64 via RawFileReader 8.0.37; `DOTNET_ROLL_FORWARD` for net8→net9/10; no Rosetta/mzLib.

### Key Artifacts

- `refs/_findings/mzpeak_groundtruth_schema.md` — chunk schema (mz_chunk_start/end, mz_chunk_values, chunk_encoding, mz_numpress_linear_bytes).
- `tools/e2e/` — corpus comparison harness (97 RAW↔mzpeak pairs) for VER2 re-verification.
- `~/Claude/mzPeakValidator`, `~/Claude/mzML2mzPeak` — conformance oracle + differential reference.

### Backlog

- BL-01: richer mzLib MGF validation (mzLib x64-only) — `.planning/BACKLOG.md`.
- Deferred debug: E2E `DIFF`/`COMPARE_ERROR`/`CONVERT_FAIL` findings from the v1 sweep (ROB-01/VER2 address some).

---
*Last updated: 2026-06-14 after v2 milestone planning*
