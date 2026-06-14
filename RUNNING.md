# Running the mzPeak writer

`-f mzpeak` converts a Thermo `.raw` file into a HUPO-PSI mzPeak archive (a ZIP of Apache
Parquet facets + `mzpeak_index.json`).

## Build

```bash
DOTNET_ROLL_FORWARD=LatestMajor DOTNET_ROLL_FORWARD_TO_PRERELEASE=1 \
  dotnet build -c Release ThermoRawFileParser/ThermoRawFileParser.sln
# output DLL: ThermoRawFileParser/bin/x64/Release/net8.0/ThermoRawFileParser.dll
```

The project targets `net8.0` and pins `PlatformTarget=x64` because the vendor library
`ThermoFisher.CommonCore.RawFileReader.dll` is x64-only. `DOTNET_ROLL_FORWARD` lets the x64
**.NET 8** build/run resolve against a newer installed x64 runtime when 8.0 isn't present.

## Run

```bash
dotnet ThermoRawFileParser/bin/x64/Release/net8.0/ThermoRawFileParser.dll \
  -i input.raw -b output.mzpeak -f mzpeak
```

`-i` input RAW, `-b` output file, `-f mzpeak` (or `-f 4`). Produces a STORED ZIP with
`spectra_data`/`spectra_peaks`/`spectra_metadata` + `chromatograms_data`/`chromatograms_metadata`
Parquet facets and `mzpeak_index.json`.

## Apple Silicon (arm64) — requires Rosetta

Thermo's `RawFileReader.dll` is **x64-only**, so on Apple-Silicon Macs TRFP (every output format,
not just mzPeak) must run as an x64 process under Rosetta 2. Native arm64 fails with
*"The assembly architecture is not compatible with the current process architecture."*

One-time setup of an x64 .NET 8 runtime:

```bash
curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 8.0 --runtime dotnet --install-dir "$HOME/.dotnet-x64" --architecture x64
curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 8.0 --runtime aspnetcore --install-dir "$HOME/.dotnet-x64" --architecture x64
```

Then build and run x64 via Rosetta:

```bash
# build (arm64 SDK can emit the x64 build with roll-forward)
DOTNET_ROLL_FORWARD=LatestMajor DOTNET_ROLL_FORWARD_TO_PRERELEASE=1 \
  dotnet build -c Release ThermoRawFileParser/ThermoRawFileParser.sln

# run under Rosetta against the x64 net8 runtime
arch -x86_64 "$HOME/.dotnet-x64/dotnet" \
  ThermoRawFileParser/bin/x64/Release/net8.0/ThermoRawFileParser.dll \
  -i input.raw -b output.mzpeak -f mzpeak
```

For `dotnet test` on Apple Silicon: `DOTNET_ROOT_X64=$HOME/.dotnet-x64 dotnet test ...`.

Intel macOS, Linux, and Windows (all x64) run natively with the plain `dotnet ...` command above.

## Validate the output

```bash
mzpeak-validate output.mzpeak            # from ~/Claude/mzPeakValidator; exit 0 = conformant
```

## End-to-end corpus comparison

`tools/e2e/` compares our output against the reference `mzML2mzPeak` corpus for every Thermo RAW
that has a matching reference `.mzpeak`. See `tools/e2e/README.md`.
