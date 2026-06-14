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

## Requirements

### Validated

<!-- Existing TRFP capabilities we build on, confirmed by the codebase. -->

- ✓ TRFP parses Thermo `.raw` and exposes per-scan m/z/intensity, RT, MS level, precursor info, centroid flag — existing
- ✓ Pluggable writer architecture (`ISpectrumWriter`/`SpectrumWriter`, format dispatch in `RawFileParser`) — existing
- ✓ Parquet.Net v5.0.1 dependency + a flat-schema `ParquetSpectrumWriter` reference — existing
- ✓ CV/ontology dictionaries (`OntologyMapping`) for instrument/analyzer/ionization/dissociation terms — existing

### Active

- [ ] Emit a valid mzPeak ZIP archive (`mzpeak_index.json` + spectra/chromatogram Parquet facets)
- [ ] Point-layout spectra data (`spectrum_index:u64, mz:f64, intensity:f32`), m/z sorted ascending
- [ ] Packed-parallel-table spectra metadata (spectrum / scan / precursor / selected_ion structs)
- [ ] File-level metadata block (file_description, instrument_configuration, software, data_processing) via CV mappings
- [ ] TIC chromatogram facet
- [ ] `--format mzpeak` wired through CLI dispatch
- [ ] Round-trip validation against the reference Python/Rust mzPeak reader

### Out of Scope

- Chunked layout, Numpress, delta-encoding — v2; point layout is spec-valid and simplest
- Null-marking / zero-run stripping — v2 optimization, not required for validity
- imzML / imaging (spatial) extension — not applicable to Thermo LC-MS; the Rust reference's imaging path is ignored
- mzPeak → RAW reverse conversion — out of scope; TRFP is one-directional
- Ion-mobility (FAIMS) full modeling — capture CV value if cheap, otherwise defer

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
| Private derivative repo okohlbacher/mzPeak4TRFP, TRFP vendored | GitHub can't make a private fork of a public repo; vendoring keeps planning paths + provenance | — Pending |

---
*Last updated: 2026-06-14 after initialization*
