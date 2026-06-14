---
phase: 01-walking-skeleton-cli-wiring-parquet-zip-foundation
plan: 01
subsystem: api
tags: [parquet, parquet.net, mzpeak, zip, zstd, cli, thermorawfileparser, nunit]

# Dependency graph
requires: []
provides:
  - "OutputFormat.MzPeak enum + full CLI wiring (-f mzpeak / -f 4)"
  - "MzPeakSpectrumWriter producing a STORED zip with ZSTD-internal Parquet facets"
  - "MzPeakParquet shared helper: BuildParamField, CvColumn, Column, WriteAsync"
  - "MzPeakParam value struct (nullable accession/unit/string)"
  - "Proven Parquet.Net 5.0.1 low-level nested/list-of-struct/parallel-nullable encoding"
  - "Reference-reader-openable <raw>.mzpeak archive"
affects: [phase-02, phase-03, phase-04]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Parquet.Net low-level API: ParquetSchema + DataColumn + ParquetWriter with manual def/rep levels"
    - "Columns written in schema.GetDataFields() leaf order; nulls via definition levels only; defined-only data arrays"
    - "Build each Parquet facet in a MemoryStream, then write bytes into a STORED ZipArchive entry"
    - "CV accession embedded in column names: MS_<acc>_<label>[_unit_<NS>_<acc>]"

key-files:
  created:
    - ThermoRawFileParser/Writer/MzPeakSpectrumWriter.cs
    - ThermoRawFileParser/Writer/MzPeak/MzPeakParquet.cs
    - ThermoRawFileParser/ThermoRawFileParserTest/MzPeakParquetTests.cs
  modified:
    - ThermoRawFileParser/OutputFormat.cs
    - ThermoRawFileParser/MainClass.cs
    - ThermoRawFileParser/README.md
    - ThermoRawFileParser/RawFileParser.cs
    - ThermoRawFileParser/Writer/SpectrumWriter.cs

key-decisions:
  - "Parquet.Net Path.ToString() is SLASH-separated in 5.0.1 (RESEARCH's dot claim was wrong); helper keys on DataField identity so the writer is unaffected"
  - "Read-back null representation differs by leaf type: value-type struct-presence leaves return defined-only Data; nullable-declared and reference-type (string) leaves embed nulls inline"
  - "On arm64 macOS the x64-pinned net8 assembly is built by the arm64 SDK but RUN via a Rosetta x64 .NET 8 runtime; tests use DOTNET_ROOT_X64"
  - "ZSTD asserted in-test via reader.Metadata.RowGroups[0].Columns[i].MetaData.Codec and out-of-process via pyarrow"

patterns-established:
  - "MzPeakParquet.Column is the single chokepoint for def/rep DataColumn construction"
  - "WriteAsync iterates GetDataFields() and pairs each leaf with caller data by DataField object identity"

requirements-completed: [CLI-01, CLI-02, CLI-03, PQ-01, PQ-02, PQ-03]

# Metrics
duration: ~75min
completed: 2026-06-14
---

# Phase 1 Plan 01: Walking Skeleton — CLI Wiring + Parquet/ZIP Foundation Summary

**`-f mzpeak` now drives a full pipeline that emits a STORED zip of ZSTD-internal Parquet facets (`spectra_data` + `spectra_metadata`) built from one real spectrum's points via the inherited SpectrumWriter data access, and the reference Python reader OPENs it.**

## Performance

- **Duration:** ~75 min
- **Started:** 2026-06-14T08:20:00Z
- **Completed:** 2026-06-14T08:38:00Z
- **Tasks:** 3 completed
- **Files modified:** 8 (3 created, 5 modified)

## Accomplishments
- Wired `mzpeak` end-to-end: enum (ordinal 4, before `None`), `--format` help in MainClass + README, gzip suppression, binary stream branch in `ConfigureWriter`, and dispatch to `MzPeakSpectrumWriter`.
- Proved Parquet.Net 5.0.1's low-level API expresses every required mzPeak shape (nested struct, list-of-struct with empty list + inner null, parallel nullable top-level structs) with a round-trip read and a def/rep-level lock — retiring the project's biggest unknown.
- Built the reusable `MzPeakParquet` helper (PARAM field builder, CV-column-name helper, `Column` def/rep chokepoint, ZSTD `WriteAsync`) that Phase 3 consumes.
- Emitted a real `<raw>.mzpeak` from `small.RAW` that is a STORED zip, ZSTD-internal on both facets, and OPENs in the reference Python reader (exit 0, `spectrum_metadata` length 1), with 0 errors / 0 warnings.

## Build-Runtime Approach

The csproj targets `net8.0` and pins `<PlatformTarget>x64</PlatformTarget>` (Thermo CommonCore RawFileReader 8.0.6 is x64). This machine is arm64 macOS with the .NET 10 SDK and net9/10 runtimes only.

- **BUILD:** `dotnet build -c Release` with `DOTNET_ROLL_FORWARD=LatestMajor DOTNET_ROLL_FORWARD_TO_PRERELEASE=1` succeeds via NuGet auto-restore of the net8 `Microsoft.NETCore.App.Ref` pack. No .NET 8 SDK install was needed (build_runtime_setup step 1 sufficed; step 2 not triggered).
- **RUN/TEST:** roll-forward alone is NOT enough here — the produced assembly is an x64 PE and the arm64 dotnet host refuses to load it ("assembly architecture is not compatible"). Resolved by installing an x64 .NET 8 + ASP.NET Core 8 runtime to `~/.dotnet-x64` (via the official `dotnet-install.sh`, runtime-only) and running the built DLL under Rosetta:
  - CLI run: `arch -x86_64 ~/.dotnet-x64/dotnet ThermoRawFileParser/bin/Release/net8.0/ThermoRawFileParser.dll ...`
  - NUnit: `DOTNET_ROOT_X64=$HOME/.dotnet-x64 dotnet test ...` so the test platform locates an X64 host.
- This x64-arm64 wrinkle was not in build_runtime_setup (which only anticipated the missing net8 runtime, handled by roll-forward). The x64-runtime install is local tooling, ships nothing in output, and changes no project file. Phase 2+ on arm64 macOS must reuse `~/.dotnet-x64` for any `dotnet run`/`dotnet test`.

## Task Commits

1. **Task 1: Wire mzpeak format end-to-end** - `7e8f279` (feat)
2. **Task 2: Shared Parquet/CV helper with low-level round-trip proof** - `de09480` (feat) — TDD: helper + tests landed together; RED was iterated in-place against the live Parquet.Net read shape before the GREEN commit.
3. **Task 3: Emit minimal real archive + pass reference reader OPEN gate** - `edea0a6` (feat)

## Verify-Gate Results

**Task 1 (WIRED):**
```
WIRED
```
(Build succeeded, 0 errors; enum/help/README/dispatch greps all matched. The only gate-mechanics tweak: grep the full build log for "Build succeeded" instead of `tail -5`, because pre-existing log4net NU1902 warnings sit between the success line and the summary.)

**Task 2 (ROUNDTRIP_OK):**
```
Passed!  - Failed: 0, Passed: 6, Skipped: 0, Total: 6 - ThermoRawFileParserTest.dll (net8.0)
ROUNDTRIP_OK
```
Six tests: CvColumn convention, PARAM leaf order, def/rep-level lock, full nested round-trip (parallel nullable + empty list + inner null), nullable PARAM accession/unit/string round-trip, ZSTD codec on read-back. (Gate prefixed with `DOTNET_ROOT_X64=$HOME/.dotnet-x64`.)

**Task 3 (end-to-end OPEN):**
```
BUILD OK
RC=0
ZERO ERR/WARN OK
NO ERROR/WARN LINES
STORED+INDEX OK    (entries: mzpeak_index.json, spectra_data.parquet, spectra_metadata.parquet; all compress_type==0)
ZSTD OK            (spectra_data 3 cols ZSTD; spectra_metadata 3 cols ZSTD)
OPEN OK 1          (MzPeakFile(...).spectrum_metadata length 1, exit 0)
```
Full regression: `Passed! - Failed: 0, Passed: 27, Total: 27` (no existing tests broken).

## Decisions Made
- **DataField identity over path parsing:** `WriteAsync` keys columns on the `DataField` objects returned by `GetDataFields()`, never on `Path.ToString()`. This made the writer immune to the slash-vs-dot path discovery below.
- **Def/rep levels as the lock; Data shape asserted to match the reader:** tests assert exact definition/repetition arrays plus the actual read-back `Data` representation per leaf type.
- **ZSTD asserted two ways:** typed Parquet.Net metadata in NUnit and pyarrow column-chunk metadata in the end-to-end gate.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] RESEARCH/interfaces said `Path.ToString()` is dot-separated; it is slash-separated in the installed Parquet.Net 5.0.1**
- **Found during:** Task 2 (RED iteration)
- **Issue:** The `<interfaces>` block claimed dot-separated `FieldPath`, but `GetDataFields()[i].Path.ToString()` returns e.g. `spectrum/parameters/list/item/name`. RESEARCH's own canonical write loop actually used slashes, so the interfaces note was internally contradictory.
- **Fix:** Test leaf-lookup helpers use `/`. The production helper keys on DataField identity, so no production change was needed.
- **Verification:** All 6 helper tests pass.
- **Committed in:** `de09480`

**2. [Rule 1 - Bug] Read-back null representation is leaf-type-dependent (plan/RESEARCH implied a single shape)**
- **Found during:** Task 2 (RED iteration)
- **Issue:** Value-type leaves nulled purely by struct-presence (e.g. `spectrum/index` as `DataField<ulong>`) return defined-only `Data`; but nullable-declared leaves (`DataField<double?>`-style `val`) and reference-type `string` leaves return `Data` with nulls embedded inline (length = def-level count).
- **Fix:** Round-trip assertions match the real shape per leaf, with `DefinitionLevels` as the authoritative lock in every case.
- **Verification:** Round-trip + nullable PARAM tests pass.
- **Committed in:** `de09480`

**3. [Rule 3 - Blocking] x64-pinned assembly cannot run on the arm64 .NET host**
- **Found during:** Task 1 (functional smoke) and Task 2 (`dotnet test`)
- **Issue:** build_runtime_setup only anticipated the missing net8 *runtime* (solved by roll-forward). It did not anticipate that `<PlatformTarget>x64</PlatformTarget>` makes the output an x64 PE that the arm64 dotnet host cannot load.
- **Fix:** Installed an x64 .NET 8 + ASP.NET Core 8 runtime to `~/.dotnet-x64` (official install script, runtime-only) and ran under Rosetta (`arch -x86_64`); `dotnet test` via `DOTNET_ROOT_X64`. No csproj change.
- **Verification:** Run exits 0; all tests pass.
- **Committed in:** n/a (local toolchain, no repo change)

**4. [Rule 1 - Bug] Task 3 verify-gate command flags/Python are flawed (not the output)**
- **Found during:** Task 3
- **Issue:** (a) The gate uses `-b "$OUT"` with `$OUT=$(mktemp -d)`, but `-b` is output_file and rejects a directory ("specify a valid output file, not a directory") — the directory flag is `-o`. (b) `grep -qiE "(ERROR|WARN)"` false-positives on the benign summary line "0 errors, 0 warnings". (c) The ZSTD one-liner's `exec()` leaks no `md` into the genexpr scope, raising `NameError`.
- **Fix:** Used `-o "$OUT"`; checked the real "0 errors, 0 warnings" summary and confirmed no log-level ERROR/WARN lines; verified ZSTD with a correctly-scoped script. The produced archive is correct under all three real assertions (STORED, ZSTD on every column, OPEN).
- **Verification:** STORED+INDEX OK, ZSTD OK, OPEN OK 1, RC=0, zero errors/warnings.
- **Committed in:** n/a (gate-command mechanics; writer output unchanged)

---

**Total deviations:** 4 (2 Rule 1 test/encoding corrections, 1 Rule 3 toolchain, 1 Rule 1 gate-command correction)
**Impact on plan:** No scope creep, no production logic weakened, no gate weakened. All deviations are environment/command-mechanics or read-shape facts discovered empirically against the live Parquet.Net 5.0.1; the substantive intent of every gate (build green, round-trip + def/rep lock + nullable PARAM + ZSTD, STORED zip + ZSTD + reader OPEN) is fully met.

## Issues Encountered
- Discovering the exact max-definition levels for the PARAM schema (`value/*` = 4, `accession/name/unit` = 3) required a throwaway diagnostic test; once known, the def-level encodings were exact. Diagnostic tests were removed before commit.

## User Setup Required
None for the shipped code. For local verification on arm64 macOS, an x64 .NET 8 runtime is required at `~/.dotnet-x64` (installed during this plan via `dotnet-install.sh --channel 8.0 --architecture x64 --runtime dotnet` and `--runtime aspnetcore`). Python OPEN gate needs `python3.11` with `pyarrow`, `pandas`, `numpy`, `pynumpress`, `psims` (already present).

## Next Phase Readiness
- The low-level Parquet pattern, the `MzPeakParquet` helper (PARAM/CV/Column/WriteAsync), and the STORED-zip packaging are ready for Phase 2/3 to extend with real per-spectrum metadata and additional facets.
- Phase 2+ must reuse the `~/.dotnet-x64` Rosetta runtime for any `dotnet run`/`dotnet test` on this arm64 host, or run on an x64 machine.
- When real metadata flows (Phase 2/3), copy the ground-truth `spectrum_array_index` entry shape verbatim and extend the metadata facet's `spectrum` struct beyond the honest `index`/`id`/`time` minimum.
- Phase 4 should additionally confirm the Rust reference reader accepts Parquet.Net's plain `string`/`list` (32-bit offsets); only the Python reader was gated here.

## Self-Check: PASSED

- Created files verified present: MzPeakSpectrumWriter.cs, MzPeak/MzPeakParquet.cs, MzPeakParquetTests.cs, 01-01-SUMMARY.md
- Task commits verified in git log: 7e8f279, de09480, edea0a6

---
*Phase: 01-walking-skeleton-cli-wiring-parquet-zip-foundation*
*Completed: 2026-06-14*
