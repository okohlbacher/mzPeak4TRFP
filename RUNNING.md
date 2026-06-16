# Running the mzPeak writer

`-f mzpeak` converts a Thermo `.raw` file into a HUPO-PSI mzPeak archive (a ZIP of Apache
Parquet facets + `mzpeak_index.json`).

## Build

```bash
DOTNET_ROLL_FORWARD=LatestMajor DOTNET_ROLL_FORWARD_TO_PRERELEASE=1 \
  dotnet build -c Release ThermoRawFileParser/ThermoRawFileParser.sln
# output DLL: ThermoRawFileParser/bin/x64/Release/net8.0/ThermoRawFileParser.dll
```

The project targets `net8.0` and builds **AnyCPU** against `ThermoFisher.CommonCore.RawFileReader`
**8.0.37** (the AnyCPU build — vendored under `vendor/thermo-nuget/`, see `nuget.config`).
`DOTNET_ROLL_FORWARD` lets the **.NET 8** build/run resolve against a newer installed runtime when
8.0 isn't present.

## Run

```bash
dotnet ThermoRawFileParser/bin/x64/Release/net8.0/ThermoRawFileParser.dll \
  -i input.raw -b output.mzpeak -f mzpeak
```

`-i` input RAW, `-b` output file, `-f mzpeak` (or `-f 4`). Produces a STORED ZIP with
`spectra_data`/`spectra_peaks`/`spectra_metadata` + `chromatograms_data`/`chromatograms_metadata`
Parquet facets and `mzpeak_index.json`.

## Encoding options (mzPeak only)

The default output is the reference **chunked layout** with **lossy Numpress-linear m/z** (bounded
~5e-7 Th; intensity stays lossless `f32`). The chosen encodings are self-described in the archive's
`data_processing_method_list`, so the output records its own transforms. Flags:

| Flag | Effect | Fidelity |
|------|--------|----------|
| *(none)* | Chunked layout, Numpress-linear m/z (default) | L2: m/z bounded ~5e-7 Th, intensity exact |
| `--lossless` (alias `--no-numpress`) | Chunked layout, delta-encoded m/z (`MS:1003089`) instead of Numpress | L1: exact (m/z, intensity) multiset |
| `--point` | v1 point layout (`spectra_data` as `point<spectrum_index, mz, intensity>`); overrides Numpress | L1: exact (m/z, intensity) multiset |
| `--chunk-size=<Th>` | m/z window width for chunked layouts (default `50.0`) | — |

The default lossy mode prints a one-line warning at startup; `--lossless` or `--point` silence it.
`--chunk-size` applies only to the chunked spectra layouts (ignored under `--point`).

`chromatograms_data` follows the same layout choice: the chunked modes emit the TIC as a single
time-axis chunk (numpress-linear or delta time, f64 intensity, matching the reference); `--point`
emits the legacy per-point chromatogram with a per-point ms_level.

### Vendor metadata (`--vendor-metadata`)

Thermo `.raw` files carry far more metadata than mzML's CV vocabulary can express (e.g. an Orbitrap
Astral run exposes ~85 per-scan "Trailer Extra" fields, ~80 of which have no mzML CV term, plus tune
data with the mass-calibration vector, a per-RT status log, and the instrument method text).
`--vendor-metadata` captures it **verbatim** as additive, non-CV facets:

| Facet | Shape | Content |
|-------|-------|---------|
| `vendor_scan_trailers.parquet` | tall: `(scan_index, label, value, value_float)` | the complete per-scan Trailer Extra bag, one row per (scan, label) |
| `vendor_file_metadata.parquet` | tall: `(category, entry_index, label, value, value_float)` | instrument / sample / run_header / tune / status-log-header / instrument_method |
| `vendor_trailer_schema.parquet` | `(ordinal, label, data_type)` | the trailer header — lets a consumer pivot the tall trailers into a typed wide view |

Every `value` is the exact source string; `value_float` is a best-effort numeric parse (null when
non-numeric) so the tables are directly queryable. The tall layout is verbatim, schema-stable across
instruments/methods, and concatenates trivially for cross-run QC. These facets are listed in
`mzpeak_index.json` with `entity_type: "vendor"` and are ignored by `mzpeak-validate` (0 errors).

```bash
trfp -i input.raw -b output.mzpeak -f mzpeak --vendor-metadata
```

```bash
# exact (lossless) chunked output
dotnet ThermoRawFileParser/bin/x64/Release/net8.0/ThermoRawFileParser.dll \
  -i input.raw -b output.mzpeak -f mzpeak --lossless

# v1 point layout, 25 Th would-be windows ignored (point layout has no windows)
dotnet ... -f mzpeak --point
```

## Apple Silicon (arm64) — runs natively

With RawFileReader **8.0.37** (AnyCPU) the writer runs **natively on arm64** — no Rosetta needed.
The build/run commands above work as-is on Apple Silicon (the `DOTNET_ROLL_FORWARD` env lets the
net8 build run on an installed net9/net10 arm64 runtime).

The only x64-only piece left is a **test-only** dependency: the MGF round-trip tests use mzLib's
`MassSpectrometry.dll`, which is x64-only, so `TestMgf`/`TestFolderMgfs` self-`Ignore` on arm64
(the writer's own tests, the differential, and `mzpeak-validate` all run natively). To exercise the
MGF tests too, run the suite under Rosetta with an x64 .NET 8 runtime:

```bash
curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 8.0 --runtime dotnet --install-dir "$HOME/.dotnet-x64" --architecture x64
DOTNET_ROOT_X64=$HOME/.dotnet-x64 arch -x86_64 "$HOME/.dotnet-x64/dotnet" test ...
```

Intel macOS, Linux, and Windows run natively as well.

## Validate the output

```bash
mzpeak-validate output.mzpeak            # from ~/Claude/mzPeakValidator; exit 0 = conformant
```

## End-to-end corpus comparison

`tools/e2e/` compares our output against the reference `mzML2mzPeak` corpus for every Thermo RAW
that has a matching reference `.mzpeak`. See `tools/e2e/README.md`.
