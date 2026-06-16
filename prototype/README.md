# Prototype: verbatim Thermo RAW metadata → Parquet

> **Now integrated** into the mzPeak writer as `--vendor-metadata` (emits `vendor_scan_trailers` /
> `vendor_file_metadata` / `vendor_trailer_schema` facets — see `RUNNING.md`). These standalone tools
> remain for ad-hoc probing/export outside the mzPeak archive.


Thermo `.raw` files carry far more instrument metadata than the HUPO-PSI mzML controlled vocabulary
can represent. This prototype (a) **probes** the full metadata surface and (b) **exports it verbatim**
into Parquet tables that could become `vendor_*` facets inside an mzPeak archive.

## What mzML cannot represent (measured on the 22 GB Astral)

`20231206_HAP1_1ug_60min_DIA_2Th_5e4_3p5ms_rep03.raw` (Orbitrap Astral, 744,651 spectra):

| Source (IRawDataPlus) | Size | mzML CV coverage |
|---|---|---|
| **Trailer Extra** (per scan) | **85 labels** × 744,651 scans | ~6 have CV terms (injection time, charge, monoisotopic m/z, master scan, FT resolution, FAIMS CV). **~79 do not** — Conversion Parameter B/C, Temperature/RF/Space-Charge/Resolution Comp (ppm), Astral Mass Stabilization (ppm), Funnel RF Level, OT Intens Comp Factor, RawOvFtT, AGC Strategy/History/References, PrOSA NumF/Comp/ScScr, t0 FLP, … |
| **Tune Data** (file-level) | **186** entries | spray/gas/source settings + **35 Mass Calibration Parameters** (e.g. `1.48758434e-010`). No CV. |
| **Status Log** (per-RT timeseries) | **200 labels × 428 timepoints** | C-trap RF, lens voltages, quad DC, source HV, FAIMS device status. No CV. |
| **Instrument Method** | 2 texts (4.8k + 12.5k chars) | free text. No CV. |
| **Sample Info / RunHeaderEx** | vial, method path, error/tune/status counts | partial. |

mzML's only escape hatch is `<userParam>` (untyped name/value), which ProteoWizard uses sparingly;
TRFP's `--metadata` emits a small fixed summary. None of these preserve the per-scan 85-field trailer
bag, the tune mass-cal vector, or the status-log timeseries.

## Two tools

- **`RawMetaProbe`** — reflection-based enumeration of the entire IRawDataPlus metadata API + a dump of
  instrument/sample/run-header/trailer/tune/status/method, to see exactly what exists. Metadata-only,
  so it is instant even on the 22 GB file.
  ```
  dotnet run --project prototype/RawMetaProbe -c Release -- <file.raw> [scan]
  ```

- **`RawMetaExport`** — writes the metadata **verbatim** to Parquet:
  ```
  dotnet run --project prototype/RawMetaExport -c Release -- <file.raw> <out-dir> [--max-scans N]
  ```
  - `file_metadata.parquet` — `(category, entry_index, label, value, value_float)` for
    instrument / sample / run_header / tune[*] / status_log_header / instrument_method.
  - `scan_trailers.parquet` — `(scan_index, label, value, value_float)`, one row per (scan, label):
    the complete per-scan Trailer Extra bag.

  Every `value` is the **exact source string** (verbatim — scientific notation, locale decimals,
  Windows paths preserved); `value_float` is a best-effort numeric parse (null when non-numeric) so
  the table is directly queryable.

## Verified

- small.RAW (full): `file_metadata` 655 rows; `scan_trailers` 1,248 rows (48 × 26).
- Astral (`--max-scans 2000`): `file_metadata` 441 rows; `scan_trailers` 170,000 rows (85 labels) in
  ~0.5 s. pyarrow round-trip confirms verbatim strings + typed `value_float`.
- Full Astral `scan_trailers` would be ~63M tall rows (744,651 × 85) — feasible streamed (1 M-row
  groups, ZSTD); the `--max-scans` cap keeps the prototype fast.

## Design notes / next steps

- **Tall vs wide:** the tall `(scan_index, label, value)` shape is fully verbatim and robust to
  per-instrument label variation. The trailer **header** (`GetTrailerExtraHeaderInformation`) is stable
  within a run, so a *wide* variant (one column per label, typed from the header's DataType) is a
  natural analytics-friendly alternative.
- **mzPeak integration:** emit these as `vendor_scan_trailers.parquet` + `vendor_file_metadata.parquet`
  facets in the archive, listed in `mzpeak_index.json`, gated behind a `--vendor-metadata` flag — no
  impact on conformance (they are additive, non-CV facets).
- **Status-log timeseries** (per-RT) is omitted from the export here (only the header snapshot is
  captured); a `vendor_status_log.parquet` `(position, rt, label, value)` table is the obvious addition.
