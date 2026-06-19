# mzPeak Writer for ThermoRawFileParser

## What This Is

An additional output format ("mzPeak") for the C#/.NET ThermoRawFileParser (TRFP) that
converts Thermo `.raw` acquisitions directly into a valid HUPO-PSI **mzPeak** archive — a
ZIP of Apache Parquet facets plus a JSON index. It sits alongside TRFP's existing MGF, mzML,
and Parquet writers and is selectable via `--format mzpeak`. The target audience is
proteomics/metabolomics users who want the smaller, columnar, query-friendly mzPeak format
without a two-step `raw → mzML → mzpeak` conversion.

## Core Value

Produce a **spec-valid mzPeak archive readable by the reference Rust/Python readers**, with no
loss of spectral information (m/z + intensity survive at the canonical mzPeak width), straight
from Thermo RAW.

## ⚠️ Architecture (current — 2026-06 pivot)

The original plan built a **bespoke** mzPeak writer (v1 point layout, then v2 chunked + Numpress).
That bespoke writer has been **deleted**. TRFP now **delegates** all spectrum/chromatogram/standard-
metadata writing to the official HUPO-PSI **mzPeak.NET** library, vendored under `mzpeak.net/`
(`MZPeakNet` + `MZPeakNet.Thermo`). `MzPeakSpectrumWriter` is now a thin orchestrator over
`MZPeak.Thermo.ThermoMZPeakWriter`. TRFP-owned code is limited to the scan-loop orchestration and the
**verbatim vendor-metadata facets** (Thermo trailer/tune/run-header/status-log/error-log) that mzML's
CV vocabulary cannot represent. Rationale: track the reference implementation rather than maintain a
parallel format writer. Default m/z encoding is now **lossless Float64** (library default), not the
v2 lossy-Numpress plan. See `STATE.md` (v2→v3 pivot table) and `METADATA-MAPPING.md` (handoff).

## Requirements

### Validated

<!-- Existing TRFP capabilities we build on, confirmed by the codebase. -->

- ✓ TRFP parses Thermo `.raw` and exposes per-scan m/z/intensity, RT, MS level, precursor info, centroid flag — existing
- ✓ Pluggable writer architecture (`ISpectrumWriter`/`SpectrumWriter`, format dispatch in `RawFileParser`) — existing
- ✓ Parquet.Net v5.0.1 dependency + a flat-schema `ParquetSpectrumWriter` reference — existing
- ✓ CV/ontology dictionaries (`OntologyMapping`) for instrument/analyzer/ionization/dissociation terms — existing

### Validated (v1 — shipped & certified 2026-06-14, archived under `.planning/archive/v1-point-layout/`)

- ✓ `--format mzpeak` end-to-end; valid mzPeak ZIP (index + spectra/chromatogram Parquet facets) — v1
- ✓ Point-layout `spectra_data`/`spectra_peaks` (dual representation), m/z sorted, canonical widths — v1
- ✓ Packed-parallel-table `spectra_metadata` (spectrum/scan/precursor/selected_ion) + file-level metadata/index — v1
- ✓ TIC chromatogram facets — v1
- ✓ `mzpeak-validate` PASS (0/0); differential vs mzML2mzPeak (exact multiset on matched corpus pairs) — v1
- ✓ Native arm64 (RawFileReader 8.0.37 AnyCPU), 52/52 tests, no Rosetta — post-v1

### Active (v2 — "compression, fidelity & scale")

- [ ] **Chunked layout** (default) — `spectra_data`/`spectra_peaks` as chunk facets matching the reference schema
- [ ] **Numpress-linear m/z** (default) — `mz_numpress_linear_bytes` + recorded transform CURIE; `--lossless`/`--no-numpress` opt-out
- [ ] **Null-marking / zero-run stripping** for profile data (with the δmz model in `spectrum.mz_delta_model`)
- [ ] **Ion-mobility values** — populate `ion_mobility_value`/`type` from Thermo FAIMS CV (columns already exist, null in v1)
- [ ] **Streaming writer** — bounded row-group writes (constant memory) to convert multi-GB RAW without OOM
- [ ] **Per-scan robustness** — tolerate individual scan read failures (continue + log) instead of aborting the archive
- [ ] **Conformance & corpus re-verification** — validate all modes; re-run the E2E corpus (now structurally matching the reference)

### Out of Scope

- imzML / imaging (spatial) extension — not applicable to Thermo LC-MS; the Rust reference's imaging path is ignored
- mzPeak → RAW reverse conversion — TRFP is one-directional
- Richer mzLib-based MGF test validation — backlog BL-01 (mzLib is x64-only)

## Context

- **Source**: `./ThermoRawFileParser` (net8.0). New writer is `Writer/MzPeakSpectrumWriter.cs`
  extending `SpectrumWriter`. Wire points: `OutputFormat.cs` enum, `MainClass.cs` help text,
  `RawFileParser.cs` dispatch switch.
- **Target spec**: `./refs/mzPeak` (HUPO-PSI reference, Rust). Ground-truth schema extracted to
  `./refs/_findings/mzpeak_groundtruth_schema.md`.
- **Mapping reference**: `./refs/mzML2mzPeak` (Rust mzML→mzPeak), non-imaging path. CV→column
  mapping bible at `./refs/_findings/mzpeak_mapping_report.md`.
- **Primary technical risk**: Parquet.Net's high-level POCO serializer (used by the existing
  Parquet writer) likely cannot express nested structs + lists-of-structs + parallel nullable
  top-level columns. The low-level `ParquetSchema`/`DataColumn` API is probably required.
  RETIRED by the Phase 1 spike — the low-level API round-trips all required shapes.
- **Validation tooling**: `~/Claude/mzPeakValidator` (`mzpeak-validate`, profile-driven, exit 0/1/2)
  is the authoritative conformance oracle. `~/Claude/mzML2mzPeak` (Rust reference converter, large
  corpus, L1/L2 conformance tests) is the differential reference: the same RAW converted via
  `RAW→(TRFP mzML)→mzML→(mzML2mzPeak)→mzpeak` must match our direct `RAW→mzpeak`.

## Constraints

- **Tech stack**: C# / .NET 8, Parquet.Net 5.0.1 (already present); ZIP via System.IO.Compression (STORED level).
- **Compatibility**: Output must open in the reference Rust/Python mzPeak readers without error.
- **Format**: m/z=float64, intensity=float32; m/z ascending; archive ZIP STORED; ZSTD inside Parquet.
- **Quality gate**: Adversarial external review (codex + vibe CLI) at the start (plan) and close
  (certification) of every phase.
- **Code style**: Compact, unbloated; no comments referencing harness/process phases.

## Key Decisions

| Decision | Rationale | Outcome |
|----------|-----------|---------|
| Point layout for v1 (no chunked/Numpress) | Spec-valid and simplest; lowest-risk path to a readable file | — Pending |
| Validate Parquet.Net nested-schema capability via spike before metadata writer | De-risks the single largest unknown | — Pending |
| Reuse existing `SpectrumWriter` data extraction + `OntologyMapping` CV dicts | Avoid re-implementing Thermo decoding and CV lookup | — Pending |
| External codex+vibe review at each phase boundary | User-mandated quality gate | — Pending |
| `mzpeak-validate` is the conformance gate (replaces ad-hoc reader OPEN) | Profile-driven, language-independent oracle; stronger than "a reader opens it" | — Pending |
| Differential equivalence vs mzML2mzPeak for the same input | Pins our mapping to the established reference converter's semantics | — Pending |
| Private derivative repo okohlbacher/mzPeak4TRFP, TRFP vendored | GitHub can't make a private fork of a public repo; vendoring keeps planning paths + provenance | ✓ Good |
| Native arm64 via RawFileReader 8.0.37 (AnyCPU), drop x64 pin + mzLib | Removes Rosetta; 52/52 tests native | ✓ Good |
| v2 default = chunked layout + Numpress-linear m/z (lossy m/z, recorded) | Smallest files, structurally matches the reference corpus; `--lossless`/`--point` opt-outs | — Pending |
| v2 includes operational work (streaming + per-scan robustness), not just format | E2E surfaced multi-GB OOM risk + a per-scan CONVERT_FAIL; needed for real-world corpus | ⊘ Superseded |
| **v3 pivot: delegate to vendored mzPeak.NET; delete the bespoke writer** | Track the official reference implementation instead of maintaining a parallel format writer; ~3k LoC writer + ~3.8k LoC tests removed | ✓ Done |
| **Default m/z = lossless Float64 (library default), not lossy Numpress** | The v2 size-via-numpress goal is dropped; chunking + zstd give the size win without lossy m/z | ✓ Done |
| **Vendor metadata is opt-in, best-effort (log + degrade, never abort)** | Flaky Thermo APIs must not fail a multi-GB conversion; verbatim data preserved where readable | ✓ Done |
| **Patch conformance bugs in the vendored mzPeak.NET; keep it otherwise upstream-clean** | 6 library bugs blocked 0/0 validation; minimal divergence keeps diffs/PRs clean (HUPO-PSI/mzPeak.NET#1) | ✓ Done |

---
*Last updated: 2026-06-18 — reconciled to the mzPeak.NET-delegation architecture (v2→v3 pivot).*
