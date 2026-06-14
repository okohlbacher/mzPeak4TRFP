# Phase 2 plan review brief (plan gate)

Adversarial review of the PLAN below. Read-only; output findings only.

## Context

Phase 1 shipped `-f mzpeak` emitting a STORED zip of ZSTD Parquet facets from ONE spectrum, with a
reusable `MzPeakParquet` low-level helper. Phase 2 makes the SIGNAL facets complete: full point-layout
`spectra_data.parquet` for all in-range spectra + a centroid `spectra_peaks.parquet`, canonical widths,
m/z sorted ascending (multiset preserved), filters honored, `sorting_rank:0` declared.

## Read (repo root /Users/kohlbach/Claude/mzPeak4TRFR)

- .planning/phases/02-spectra-signal-data/02-01-PLAN.md   ← THE PLAN
- .planning/phases/02-spectra-signal-data/CONTEXT.md
- .planning/phases/01-walking-skeleton-cli-wiring-parquet-zip-foundation/01-01-SUMMARY.md  (helper API, runtime, current writer)
- ThermoRawFileParser/Writer/MzPeakSpectrumWriter.cs, Writer/MzPeak/MzPeakParquet.cs (current code)
- ThermoRawFileParser/Writer/ParquetSpectrumWriter.cs, Writer/SpectrumWriter.cs (centroid/profile + loop patterns)
- refs/_findings/mzpeak_groundtruth_schema.md

## Evaluate

1. CORRECTNESS: Will the tasks compile/run against the current writer + helper? Is the per-scan loop /
   filtering / centroid-flag logic faithfully mirroring ParquetSpectrumWriter? Memory behaviour for large
   RAW (full MemoryStream vs row-group batching) — is the chosen approach safe and clearly specified?
2. SORT + MULTISET: Is the stable (mz,intensity) sort guaranteed to preserve the multiset under duplicate
   m/z (no merge/drop)? Is f64/f32 coercion uniform and correct?
3. CENTROID ROUTING (call this out explicitly): The plan routes each scan to spectra_data OR spectra_peaks
   based on a single `ReadMZData` read's `isCentroided`. The reference Rust archive emits BOTH profile
   (spectra_data) AND centroids (spectra_peaks) for the same scan because a Thermo scan usually has both a
   SegmentedScan and a CentroidStream. Is the single-representation choice DEFENSIBLE for a v1 whose
   conformance bar is (a) mzpeak-validate and (b) differential equivalence vs mzML2mzPeak fed by TRFP's
   OWN mzML writer (which is also single-representation)? Or will it cause a validator/differential failure?
   State the risk and whether the plan should instead read both representations.
4. CONFORMANCE: sorting_rank:0 placement in the array_index JSON; index lists spectra_peaks only when
   written; counts/CustomMetadata correct; nothing that regresses the Phase-1 validator state (only the
   expected Phase-3 scan-facet error should remain).
5. ACCEPTANCE DECISIVENESS: are the verify commands runnable and decisive? Runtime env (DOTNET_ROLL_FORWARD
   build; ~/.dotnet-x64 Rosetta run/test) embedded in the commands?
6. BLOAT/STYLE: over- or under-specification; any instruction that would add harness/process/phase comments
   or duplicate MzPeakParquet.

## Output

Prioritized findings: SEVERITY (BLOCKER/HIGH/MEDIUM/LOW/NIT), location, problem, concrete fix. Then a single
line: VERDICT: SHIP / SHIP-WITH-FIXES / REWORK.
