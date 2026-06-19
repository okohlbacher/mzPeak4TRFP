---
gsd_state_version: 1.0
milestone: v3
milestone_name: mzpeak-net-delegation
status: in-progress
last_updated: "2026-06-18"
last_activity: 2026-06-18 — TRFP mzPeak writer reconciled to the mzPeak.NET-delegation architecture; row-group sizing gap closed (FT-ICR 0/0); planning docs updated to post-refactor reality.
progress:
  total_phases: 4
  completed_phases: 3
  total_plans: 0
  completed_plans: 0
  percent: 75
---

# Project State

## Project Reference

See: .planning/PROJECT.md

**Core value:** A spec-valid mzPeak writer for ThermoRawFileParser that delegates to the official
HUPO-PSI **mzPeak.NET** library (vendored), so TRFP tracks the reference implementation instead of
maintaining a bespoke format writer — plus verbatim Thermo vendor-metadata facets that mzML's CV
vocabulary cannot represent.

## ⚠️ Architectural pivot (2026-06): v2 superseded

The v2 milestone ("compression, fidelity & scale") was built around a **bespoke** mzPeak writer.
That writer was **deleted** and TRFP now delegates all spectrum/chromatogram/standard-metadata
writing to the vendored **mzPeak.NET** library (`mzpeak.net/`, projects `MZPeakNet` +
`MZPeakNet.Thermo`). Most v2 goals are now provided by the library; the rest changed or were dropped:

| v2 phase | Status under mzPeak.NET delegation |
|---|---|
| 1 — Streaming + per-scan robustness | **Provided.** Library streams (21 GB / 744k spectra done); per-scan robustness lives in `MzPeakSpectrumWriter` (guarded read phase, skip+count bad scans). |
| 2 — Chunked layout | **Provided.** Chunked `spectra_data` is the library default; `--point` opt-out retained. |
| 3 — Numpress-linear m/z | **Dropped / diverged.** mzPeak.NET writes **lossless Float64** m/z by default (no Numpress). The v2 "lossy-numpress-default for size" goal is abandoned; size comes from chunking + zstd. The dead `--no-numpress`/`--lossless`/`--chunk-size` flags were removed. |
| 4 — Profile compaction + ion mobility | FAIMS ion-mobility is **provided** by the library (`AddScan`). Profile compaction (zero-run strip / null-mark / δmz model) is **moot** — the library owns the data encoding (was already backlog BL-02). |
| 5 — CLI/docs + conformance | **Largely delivered** (see below). |

## Current Position

Milestone: **v3 — "mzPeak.NET delegation"** (the refactor + hardening + verification)
Status: feature-complete and verified on the feature branch `raw-verbatim-metadata` (not yet merged to `main`).
Last activity: 2026-06-18 — row-group sizing gap closed; planning reconciled.

Progress: [███████░░░] ~75%

## Delivered (v3, branch `raw-verbatim-metadata`)

- **Refactor** (`d416502`): `MzPeakSpectrumWriter` delegates to `MZPeak.Thermo.ThermoMZPeakWriter`;
  ~3k LoC bespoke writer + ~3.8k LoC bespoke tests removed; mzPeak.NET vendored (AnyCPU/arm64;
  Windows-only MassPrecisionEstimator dropped; `StartProprietary{,Parquet}Entry` hooks added).
- **Vendor facets** (opt-in `--vendor-metadata[=tall|wide|both]`, `--vendor-metadata-json`):
  verbatim Thermo trailer/tune/run-header/status-log/error-log as proprietary Parquet entries.
- **6 upstream mzPeak.NET conformance bugs patched** (`9153a8d`) → output validates 0/0
  (footer counts, unsigned count columns, two required CV terms, chunk peak-count, swapped units).
  Filed as draft PR **HUPO-PSI/mzPeak.NET#1**.
- **`--point` data-point-count fix + adversarial-review hardening** (`4fa111a`): reviewed by kimi +
  vibe + codex across 3 rounds to unanimous LGTM (best-effort vendor reads now log, resource-safe
  Parquet lifecycles, delete-on-failure, unbloat).
- **Handoff doc** `METADATA-MAPPING.md` (`991d3c6`).
- **Row-group sizing fix** (`b1ec2ac`): `EntryBufferSize=500` bounds row groups for few-but-large
  (profile/FT-ICR) spectra → FT-ICR 1 warning → 0/0 (10 row groups).

## Verification status

- `mzpeak-validate` **0 errors / 0 warnings** across CLI modes (default, `--point`, vendor
  tall/wide/both, JSON sidecar), 5 Thermo instrument classes (ion trap, Velos, FT-ICR, Lumos,
  Astral), and large files (8.4 GB / 21 GB).
- Matches the official `MZPeakNet.AppTest` reference converter's validation profile.
- Round-trip vs TRFP mzML: spectrum count / MS-level / TIC exact; non-zero m/z bit-exact (lossless).
- Test suite **27/27** (down from the bespoke writer's 86 — the chunk/numpress/codec unit tests were
  deleted with the bespoke writer; kept tests exercise the real archive + differential equivalence).

## Remaining / candidates

- Merge `raw-verbatim-metadata` → `main` when ready.
- Upstream: shepherd HUPO-PSI/mzPeak.NET#1 (the 6 conformance fixes + AnyCPU + hooks).
- Optional: add a `--point` case to `MzPeakDifferentialTests` to lock the data-point-count fix.
- Optional (upstream-better): make mzPeak.NET's row-group flush truly byte-aware (`BufferedSize`
  undercounts list payloads); the current `EntryBufferSize` bound is a reliable spectrum-count proxy.

## Accumulated Context

### Key decisions (post-pivot)
- TRFP delegates to vendored mzPeak.NET; bespoke writer deleted. Vendored copy kept close to upstream
  (only conformance fixes + AnyCPU + 2 hooks) for clean diffs/PRs.
- Default m/z encoding is **lossless Float64** (the library default), not lossy Numpress. Size from
  chunking + zstd.
- Vendor metadata is **opt-in, best-effort**: extraction failures log (`Log.Warn`) and degrade the
  facet, never abort the run.
- Build/runtime: AnyCPU, native arm64 (RawFileReader 8.0.37), `DOTNET_ROLL_FORWARD` for net8→9/10.

### Key artifacts
- `METADATA-MAPPING.md` — handoff: the two-layer mapping, facet schemas, flow, quirks, file index.
- `~/Claude/mzPeakValidator` (`mzpeak-validate`), `~/Claude/mzML2mzPeak` — conformance oracle + reference.
- `tools/e2e/` — corpus comparison harness.

### Backlog
- BL-01: richer mzLib MGF validation (mzLib x64-only) — `.planning/BACKLOG.md`.
- BL-02: profile compaction (ZRS) — now moot under delegation (library owns encoding); kept for record.

---
*Last updated: 2026-06-18 — reconciled to the mzPeak.NET-delegation architecture.*
