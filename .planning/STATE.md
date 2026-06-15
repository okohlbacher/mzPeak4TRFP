---
gsd_state_version: 1.0
milestone: v2
milestone_name: compression-fidelity-scale
status: in-progress
last_updated: "2026-06-14T21:23:42.853Z"
last_activity: 2026-06-14 — Phase 3 (Numpress-Linear m/z) executed: numpress is the new default m/z encoding (MS:1002312), validator 0/0 in all three modes, full suite 86/86 green native arm64.
progress:
  total_phases: 5
  completed_phases: 3
  total_plans: 3
  completed_plans: 3
  percent: 60
---

# Project State

## Project Reference

See: .planning/PROJECT.md (updated 2026-06-14)

**Core value:** Smaller, reference-structured mzPeak output (chunked + Numpress) that scales to multi-GB RAW and is robust to imperfect scans — without regressing v1 conformance.
**Current focus:** v2 milestone — Phase 4 (ZRS null-marking)

## Current Position

Milestone: v2 ("compression, fidelity & scale") — 5 phases, sequential
Phase: 4 of 5 (ZRS null-marking) — ready to plan
Status: Phases 1-3 shipped; Phase 3 numpress-linear m/z is the new default (validator 0/0 all 3 modes)
Last activity: 2026-06-14 — Phase 3 numpress shipped; full suite 86/86 green native arm64.

Progress: [██████░░░░] 60% (v2)

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
- Phase 3: numpress-linear m/z (MS:1002312, NOT MS:1003089 which is delta) is the new default; `mz_chunk_values` null, intensity stays lossless f32 (m/z-only numpress; intensity-SLOF deferred to backlog). `--lossless`/`--point` opt-outs; numpress spectra_data ~19% smaller than delta on small.RAW (the ~63% headline needs intensity SLOF).
- Large-file fix (post-Phase-3): row-group flush is now BYTE-aware (~64MB cap), not row-count-only — chunk/list rows are fat, so the old 1M-row cap made one ~400MB unreadable row group for files >~250MB (pyarrow 'Unexpected end of stream'; validator blind spot). Byte cap → many readable row groups. Large-file chunked read-back NUnit gate added.
- Corpus E2E (v2, post-fix): 95/95 Thermo files convert + validate (0/0); 8.4GB Astral converts+validates+reads back (68 row groups, 12.9GB peak RSS — metadata still buffered). DIFFs are the SLOF-intensity+zero-strip confound, not errors. Bruker `S4_5foldGHRP.raw` removed (Thermo-only; both readers fail; byte-identical to source). Comparator (pure-Python) times out >900s on the 2.1GB file — harness speed limit (VER2-04).
- Phase 3: C# MSNumpress port accumulates `ints` in int64 (canonical uses uint32 wraparound) so m/z*fp values > 2^32 decode correctly; on-wire bytes stay byte-identical to pynumpress.

### Key Artifacts

- `refs/_findings/mzpeak_groundtruth_schema.md` — chunk schema (mz_chunk_start/end, mz_chunk_values, chunk_encoding, mz_numpress_linear_bytes).
- `tools/e2e/` — corpus comparison harness (97 RAW↔mzpeak pairs) for VER2 re-verification.
- `~/Claude/mzPeakValidator`, `~/Claude/mzML2mzPeak` — conformance oracle + differential reference.

### Backlog

- BL-01: richer mzLib MGF validation (mzLib x64-only) — `.planning/BACKLOG.md`.
- Deferred debug: E2E `DIFF`/`COMPARE_ERROR`/`CONVERT_FAIL` findings from the v1 sweep (ROB-01/VER2 address some).

---
*Last updated: 2026-06-14 after v2 milestone planning*
