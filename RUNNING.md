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
