# mzPeak4TRFP

An **explorative mzPeak writer for [ThermoRawFileParser](https://github.com/compomics/ThermoRawFileParser)** (TRFP).
Adds a new `--format mzpeak` output that converts Thermo `.raw` acquisitions directly into a valid
[HUPO-PSI mzPeak](https://github.com/HUPO-PSI/mzPeak) archive (a ZIP of Apache Parquet facets + a JSON index),
alongside TRFP's existing MGF / mzML / Parquet writers.

## Layout

- `ThermoRawFileParser/` — vendored TRFP codebase (BSD-licensed, see `ThermoRawFileParser/LICENSE`),
  extended with `Writer/MzPeakSpectrumWriter.cs` + `Writer/MzPeak/`. This is the working code.
- `.planning/` — GSD project plan, roadmap, phase plans, and adversarial-review records.
- `refs/_findings/` — extracted mzPeak ground-truth schema and the mzML→mzPeak mapping bible.
  (The full reference clones `refs/mzPeak`, `refs/mzML2mzPeak` are kept locally but gitignored.)

## Provenance

This is a private derivative of `compomics/ThermoRawFileParser` (GitHub does not permit private forks of
public repos, so the upstream is vendored rather than git-forked). Mapping and conformance are guided by
`HUPO-PSI/mzPeak`, the Rust reference converter `okohlbacher/mzML2mzPeak`, and validated with
`okohlbacher/mzPeakValidator`.

## Status

Work in progress — explorative prototype. Target: a spec-valid, validator-passing mzPeak archive (point
layout) straight from Thermo RAW, without loss of spectral information.
