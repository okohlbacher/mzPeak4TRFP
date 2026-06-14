# Phase 1: Walking Skeleton — CLI Wiring + Parquet/ZIP Foundation - Pattern Map

**Mapped:** 2026-06-14
**Files analyzed:** 5 (1 new writer, 1 new helper, 3 modified)
**Analogs found:** 5 / 5

> All paths below are absolute under `/Users/kohlbach/Claude/mzPeak4TRFR/ThermoRawFileParser/`.
> Namespace: `ThermoRawFileParser` (root), `ThermoRawFileParser.Writer` (writers/helpers in `Writer/`),
> `ThermoRawFileParser.Util` (cross-cutting structs/helpers in `Util/`).
> Target framework `net8.0`, `ImplicitUsings=disable` (every `using` must be explicit),
> `Nullable=annotations`. Parquet.Net `5.0.1` already referenced in csproj.

---

## File Classification

| New/Modified File | Role | Data Flow | Closest Analog | Match Quality |
|-------------------|------|-----------|----------------|---------------|
| `Writer/MzPeakSpectrumWriter.cs` (new) | writer/service | batch + file-I/O | `Writer/ParquetSpectrumWriter.cs` | exact (same base, same Parquet domain) |
| `Writer/MzPeakParquet*.cs` shared helper (new) | utility | transform | `Util/CVHelpers.cs` + `Writer/WriterUtil.cs` + `Util/GeneralHelpers.cs` (MZData struct) | role-match |
| `OutputFormat.cs` (modify) | config/enum | n/a | existing enum members in same file | exact |
| `MainClass.cs` (modify) | config/CLI | request-response | `f=|format=` option block (lines 529-533) | exact |
| `RawFileParser.cs` (modify) | dispatch/router | request-response | `switch (parseInput.OutputFormat)` (lines 169-184) | exact |
| `ThermoRawFileParserTest/WriterTests.cs` (Phase 4) | test | batch | `TestParquetCentroid` (lines 416-445) | exact |

---

## Pattern Assignments

### `Writer/MzPeakSpectrumWriter.cs` (new writer, batch + file-I/O)

**Analog:** `Writer/ParquetSpectrumWriter.cs` — copy its skeleton wholesale; replace only the Parquet serialization mechanism.

**Class + ctor + logger** (analog lines 36-46):
```csharp
public class ParquetSpectrumWriter : SpectrumWriter
{
    private static readonly ILog Log =
        LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

    public ParquetSpectrumWriter(ParseInput parseInput) : base(parseInput)
    {
        //nothing to do here
    }
```
- **Copy:** the `: SpectrumWriter` inheritance, the `ParseInput`-only ctor signature delegating to `base(parseInput)`, the static `ILog Log` field (identical in every writer — see also `MgfSpectrumWriter.cs:14-15`, `SpectrumWriter.cs:19-20`).
- **Change:** class name → `MzPeakSpectrumWriter`. The Parquet ctor has nothing to do; `MgfSpectrumWriter.cs:20-23` shows the alternative — mutate `ParseInput.MsLevel` in the ctor. Keep empty unless mzPeak needs level filtering.

**`Write` entry point + guard + ConfigureWriter** (analog lines 48-65):
```csharp
public override void Write(IRawDataPlus raw, int firstScanNumber, int lastScanNumber)
{
    if (!raw.HasMsData)
    {
        throw new RawFileParserException("No MS data in RAW file, no output will be produced");
    }

    ConfigureWriter(".mzparquet");
    ...
    Log.Info(String.Format("Processing {0} MS scans", +(1 + lastScanNumber - firstScanNumber)));
```
- **Copy:** the exact override signature `Write(IRawDataPlus, int firstScanNumber, int lastScanNumber)` (declared abstract in `SpectrumWriter.cs:72`), the `HasMsData` guard throwing `RawFileParserException`, the call to inherited `ConfigureWriter(extension)`.
- **Change:** extension `.mzparquet` → `.mzpeak`. **IMPORTANT:** `ConfigureWriter` (`SpectrumWriter.cs:78-103`) opens a `StreamWriter` over the output file and special-cases `OutputFormat.Parquet` (line 88) to wrap a raw `File.Create` stream (no gzip, no text encoding). The new `MzPeak` enum value will NOT match that branch and will fall into the text/gzip branches (lines 92-100), which is wrong for a binary ZIP. Plan must either (a) add an `|| ParseInput.OutputFormat == OutputFormat.MzPeak` clause to `SpectrumWriter.cs:88`, or (b) have `MzPeakSpectrumWriter` not use the base `Writer` stream at all and open its own `ZipArchive` over `File.Create(NormalizeFileName(...))`. `NormalizeFileName` is private (`SpectrumWriter.cs:105`); if the writer manages its own file, replicate the path logic: `ParseInput.OutputFile ?? Path.Combine(ParseInput.OutputDirectory, ParseInput.RawFileNameWithoutExtension) + ".mzpeak"`.

**Per-scan loop + progress + per-scan try/catch** (analog lines 67-112):
```csharp
for (var scanNumber = firstScanNumber; scanNumber <= lastScanNumber; scanNumber++)
{
    if (ParseInput.LogFormat == LogFormat.DEFAULT)
    {
        var scanProgress = (int)((double)scanNumber / (lastScanNumber - firstScanNumber + 1) * 100);
        if (scanProgress % ProgressPercentageStep == 0 && scanProgress != lastScanProgress)
        {
            Console.Write("" + scanProgress + "% ");
            lastScanProgress = scanProgress;
        }
    }
    try
    {
        int level = (int)raw.GetScanEventForScanNumber(scanNumber).MSOrder;
        if (level <= ParseInput.MaxLevel)
        {
            var scanData = ReadScan(raw, scanNumber);
            if (scanData != null && ParseInput.MsLevel.Contains(level))
                data.AddRange(scanData);
        }
    }
    catch (Exception ex)
    {
        Log.Error($"Scan #{scanNumber} cannot be processed because of the following exception: {ex.Message}");
        Log.Debug($"{ex.StackTrace}\n{ex.InnerException}");
        ParseInput.NewError();
    }
}
```
- **Copy:** the `firstScanNumber..lastScanNumber` loop, the `LogFormat.DEFAULT` + `ProgressPercentageStep` (inherited const, `SpectrumWriter.cs:27`) progress print, the `MaxLevel` / `MsLevel.Contains(level)` filters, the per-scan try/catch that logs and calls `ParseInput.NewError()` rather than aborting. Identical loop shape in `MgfSpectrumWriter.cs:40-71`.
- **Change:** accumulate into mzPeak's low-level column buffers (parallel `List<>` per `DataColumn`) instead of `List<MzParquet>`.

**Buffered flush / row-group finalize** (analog lines 104-130):
```csharp
if (data.Count >= ParquetSliceSize)
{
    var task = ParquetSerializer.SerializeAsync(data, Writer.BaseStream, opts);
    task.Wait();
    opts.Append = true;
    data.Clear();
}
...
if (data.Count > 0) { /* final row group */ }
if (ParseInput.LogFormat == LogFormat.DEFAULT) Console.WriteLine();
Writer.Flush();
Writer.Close();
```
- **Copy:** the threshold-flush idea (`ParquetSliceSize = 1_048_576`, analog line 41), the "always keep a whole scan in one row group" invariant (analog lines 100-103 comment — preserve this property for mzPeak point rows), the trailing `Console.WriteLine()` after the progress bar, the explicit `Flush()`/`Close()`.
- **Change — this is the core spike:** replace high-level `ParquetSerializer.SerializeAsync(List<POCO>)` with Parquet.Net **low-level** `ParquetSchema`/`DataColumn`/`ParquetWriter` (per CONTEXT decision — POCO serializer cannot express nested struct + list-of-struct + parallel nullable top-level columns). Write the `.parquet` facet into a `ZipArchive` entry (`CompressionLevel.NoCompression` / STORED) plus a `mzpeak_index.json` entry; ZSTD stays as the Parquet-internal codec. Note analog already sets `opts.CompressionMethod = Parquet.CompressionMethod.Zstd` (line 59) — same enum applies at the low-level writer's row-group/column options.

**Per-scan data extraction (`ReadScan`)** (analog lines 132-336):
- **Copy the data-access calls** (these are the SpectrumWriter API the new writer reuses verbatim):
  - `raw.GetFilterForScanNumber(scanNumber)` and `.MSOrder` → ms level (analog 134, 140).
  - `raw.GetScanEventForScanNumber(scanNumber)` → scan event (analog 137).
  - `new ScanTrailer(raw.GetTrailerExtraInformation(scanNumber))` then `.AsPositiveInt(...)` / `.AsDouble(...)` / `.AsBool(...)` for charge, mono m/z, isolation width, FAIMS CV (analog 150-164).
  - `raw.RetentionTimeFromScanNumber(scanNumber)` → rt (analog 166).
  - `ReadMZData(raw, scanEvent, scanNumber, centroid, charge, noise)` — inherited from `SpectrumWriter.cs:354`. Returns `MZData` struct (`Util/GeneralHelpers.cs:14-25`): `.masses` (double[]), `.intensities` (double[]), `.charges`, `.basePeakMass/Intensity`, `.isCentroided`. Centroid flag derived as `!ParseInput.NoPeakPicking.Contains((int)scanFilter.MSOrder)` (analog 286).
  - Inherited precursor helpers if mzPeak needs precursor linkage: `_precursorTree`, `_precursorScanNumbers`, `GetParentFromScanString`, `FindLastReaction`, `CalculateSelectedIonMz` (all in `SpectrumWriter.cs`, used by analog 178-277).
- **Change:** map extracted values into mzPeak column rows / PARAM structs instead of the flat `MzParquet` struct. For the skeleton, CONTEXT permits reusing one real spectrum's points rather than fabricating data — call `ReadMZData` on the first scan and emit those points.

**Constants** (analog line 41): private `const` for slice/row-group sizing lives on the writer; `ZeroDelta`, `ProgressPercentageStep` are inherited from `SpectrumWriter` (`.cs:22,27`) — do not redeclare.

---

### Shared Parquet/CV helper file (new utility, transform)

**Decision (CONTEXT):** factor a `PARAM` value-struct builder (`struct<value:struct<integer,float,string,boolean>, accession, name, unit>`) and a CV-accession-embedded column-name helper (e.g. `MS_1000511_ms_level`) into a small support file, not in the writer.

**Placement analog:**
- Cross-cutting structs/format helpers → `Util/` namespace `ThermoRawFileParser.Util` (model on `Util/GeneralHelpers.cs` which holds the `MZData`/`MZArray` structs, lines 8-25, and `Util/CVHelpers.cs` which holds CV copy/compare helpers as a `static class` + small `IEqualityComparer`).
- Writer-specific helpers → `Writer/` namespace `ThermoRawFileParser.Writer`, model on `Writer/WriterUtil.cs` (`static public class WriterUtil` with pure static methods).

**Style to copy** (from `Util/CVHelpers.cs:6-21` and `Writer/WriterUtil.cs:9-12`):
```csharp
namespace ThermoRawFileParser.Util          // or .Writer
{
    public static class WriterUtil { ... }   // pure static helpers
    struct MZData { public double[] masses; ... }   // value structs, public fields, no ctor
}
```
- **Copy:** `static class` + static methods for the column-name builder; a `struct` with public fields for the PARAM value type (matches existing `MzParquet`/`PrecursorData` structs in `ParquetSpectrumWriter.cs:13-34` and `MZData` in `GeneralHelpers.cs`). Note `OntologyMapping.cs` uses a BOM/UTF-8 header — other files do not; keep new files BOM-free to match the majority.
- **Change:** new domain — the PARAM struct mirrors mzPeak ground-truth `struct<value:struct<...>, accession, name, unit>`; the column-name helper encodes CV accession into the field name. No existing analog implements PARAM/CV-column-name semantics, so logic is net-new; only the file shape/placement/naming conventions are copied.

---

### `OutputFormat.cs` (modify, enum)

**Analog:** the enum itself (lines 3-10).
```csharp
public enum OutputFormat
{
    MGF,        // 0
    MzML,       // 1
    IndexMzML,  // 2
    Parquet,    // 3
    None        // 4
}
```
- **Copy:** the bare member style (implicit ordinals, PascalCase).
- **Change:** insert `MzPeak` as a new member. **Ordinal placement matters:** `None` is referenced by value across the code (`--format` help says "4 for None", `ParseToEnum` resolves by int). Add `MzPeak` **before** `None` so `MzPeak = 4, None = 5`, then update every "N for None" string (see MainClass below). Adding after `None` (= 5) avoids renumbering but breaks the "highest = None" reading convention. Recommend `MzPeak` before `None` and update help text — single source of ordinals is this enum.

---

### `MainClass.cs` (modify, CLI help text + parse)

**Analog:** the `f=|format=` option (lines 529-533) and the enum parse call (line 732-735).

**Option definition** (lines 529-533):
```csharp
{
    "f=|format=",
    "The spectra output format: 0 for MGF, 1 for mzML, 2 for indexed mzML, 3 for Parquet, 4 for None (no output); both numeric and text (case insensitive) value recognized. Defaults to indexed mzML if no format is specified.",
    v => outputFormatString = v
},
```
- **Copy:** nothing structural changes — only the help string.
- **Change:** edit the description to insert `4 for mzPeak` and shift `None` to `5` (consistent with the enum-ordinal decision above). No new option key is added; `MzPeak` is selected via the existing `-f`.

**Parse path** (lines 727-776) — no code change needed, but understand it:
```csharp
if (metadataFormatString == null && outputFormatString == null)
    parseInput.OutputFormat = OutputFormat.IndexMzML;       // default (line 729)
if (outputFormatString != null)
    parseInput.OutputFormat = (OutputFormat)ParseToEnum(typeof(OutputFormat), outputFormatString, "-f, --format");  // line 734
...
if (parseInput.OutputFormat == OutputFormat.Parquet) parseInput.Gzip = false;   // line 776
```
- `ParseToEnum` (lines 911-940) generically handles both numeric and case-insensitive string (`Enum.Parse(enumType, s, true)`), so `-f mzpeak` and `-f 4` both resolve automatically once the enum member exists — **no change to ParseToEnum**.
- **Change to copy:** line 776 disables gzip for `Parquet`. mzPeak is also a binary container — add an analogous guard: `if (parseInput.OutputFormat == OutputFormat.MzPeak) parseInput.Gzip = false;` (or fold into the existing condition).
- **Note:** there is a parallel option set earlier (the `Query`/`Xic` subcommand parsers around lines 495-565 are separate); the spectra `--format` lives only in `RegularParametersParsing` (line 486+). Only edit the block at 529-533.

---

### `RawFileParser.cs` (modify, dispatch)

**Analog:** the writer dispatch switch (lines 166-185).
```csharp
if (parseInput.OutputFormat != OutputFormat.None)
{
    SpectrumWriter spectrumWriter;
    switch (parseInput.OutputFormat)
    {
        case OutputFormat.MGF:
            spectrumWriter = new MgfSpectrumWriter(parseInput);
            spectrumWriter.Write(rawFile, firstScanNumber, lastScanNumber);
            break;
        case OutputFormat.MzML:
        case OutputFormat.IndexMzML:
            spectrumWriter = new MzMlSpectrumWriter(parseInput);
            spectrumWriter.Write(rawFile, firstScanNumber, lastScanNumber);
            break;
        case OutputFormat.Parquet:
            spectrumWriter = new ParquetSpectrumWriter(parseInput);
            spectrumWriter.Write(rawFile, firstScanNumber, lastScanNumber);
            break;
    }
}
```
- **Copy:** add a new `case OutputFormat.MzPeak:` block in the same style as the `Parquet` case (lines 180-183): instantiate `new MzPeakSpectrumWriter(parseInput)`, assign to `spectrumWriter`, call `.Write(rawFile, firstScanNumber, lastScanNumber)`, `break`. `firstScanNumber`/`lastScanNumber` come from `RunHeaderEx.FirstSpectrum/LastSpectrum` (lines 150-151).
- **Change:** none beyond the new case. Output path/extension is NOT derived here — it is set inside the writer via `ConfigureWriter(".mzpeak")` (or the writer's own `File.Create`), so RawFileParser stays format-agnostic.
- **Imports:** `using ThermoRawFileParser.Writer;` already present (line 10) — `MzPeakSpectrumWriter` is in that namespace, so no new using needed.

---

## Shared Patterns

### Error / warning accounting
**Source:** `ParseInput.cs:161-169` (`NewError()` / `NewWarn()` increment counters) used throughout writers (`ParquetSpectrumWriter.cs:97,155,273`; `MgfSpectrumWriter.cs:68`).
**Apply to:** every per-scan catch block in `MzPeakSpectrumWriter`. Tests assert `parseInput.Errors == 0` and `Warnings == 0` (WriterTests), so the new writer must keep both at zero on the clean `small.RAW`.

### Logging
**Source:** every writer declares `private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);` (`SpectrumWriter.cs:19-20`, `ParquetSpectrumWriter.cs:38-39`, `MgfSpectrumWriter.cs:14-15`).
**Apply to:** `MzPeakSpectrumWriter` and any helper class that logs. `Log.Info` for scan-count, `Log.Error`+`Log.Debug` in catch, `Log.Debug` for row-group writes.

### Output-path / extension derivation
**Source:** `SpectrumWriter.ConfigureWriter` (`.cs:78-103`) + private `NormalizeFileName` (`.cs:105-128`). Path = `OutputFile ?? Path.Combine(OutputDirectory, RawFileNameWithoutExtension)`, extension appended/normalized, gzip handled.
**Apply to:** the new writer. **Gap to resolve:** `ConfigureWriter` only special-cases `OutputFormat.Parquet` (line 88) for binary output; `MzPeak` must be added there OR the writer opens its own file stream (see writer section). The mzML/Parquet/MGF test-extension assertions (WriterTests `TestExtensionsNull/Full`) define the naming contract a Phase-4 mzPeak test will mirror (`small.mzpeak`).

---

## No Analog Found

| Concern | Why no analog | Planner action |
|---------|---------------|----------------|
| Low-level Parquet.Net `ParquetSchema`/`DataColumn`/`ParquetWriter` for nested struct + list-of-struct + parallel nullable columns | All existing Parquet I/O uses the **high-level** `ParquetSerializer` POCO path (`ParquetSpectrumWriter.cs:57,106`). No low-level usage exists in the repo. | Use RESEARCH.md API snippets; this is the phase's primary spike (CONTEXT open questions 1-2). |
| `ZipArchive` STORED container wrapping Parquet + `mzpeak_index.json` | No code in the repo emits a ZIP container; gzip is the only compression used (`SpectrumWriter.cs:99`). | New code; model JSON emission on existing `Newtonsoft.Json` usage (already referenced; see `XIC/JSONParser.cs`, `Writer/Metadata*.cs`). |
| PARAM value-struct + CV-accession column-name helper | No existing struct/helper encodes the mzPeak PARAM shape or accession-in-column-name convention. CV handling exists only as `CVParamType` copy/compare (`Util/CVHelpers.cs`). | Net-new logic; copy only file placement/style from `Util/CVHelpers.cs` + `Writer/WriterUtil.cs`. |

---

## Test Pattern (Phase 4 reference)

**Analog:** `ThermoRawFileParserTest/WriterTests.cs`, `TestParquetCentroid` (lines 416-445) — the closest existing test for a binary Parquet writer.
```csharp
[Test]
public void TestParquetCentroid()
{
    var tempFilePath = Path.GetTempPath();
    var testRawFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"Data/small.RAW");
    var parseInput = new ParseInput(testRawFile, null, tempFilePath, OutputFormat.Parquet);

    RawFileParser.Parse(parseInput);
    Assert.That(parseInput.Errors, Is.EqualTo(0));
    Assert.That(parseInput.Warnings, Is.EqualTo(0));

    var parquetFilePath = Path.Combine(tempFilePath, "small.mzparquet");
    Assert.That(File.Exists(parquetFilePath));
    using (var parquetReader = ParquetReader.CreateAsync(parquetFilePath).Result)
    {
        var groupReader = parquetReader.OpenRowGroupReader(0);
        var schema = parquetReader.Schema;
        var scanColumn = groupReader.ReadColumnAsync(schema.FindDataField("scan")).Result;
        Assert.That(scanColumn.NumValues, Is.EqualTo(48520));
        Assert.That(scanColumn.Statistics.DistinctCount, Is.EqualTo(48));
    }
    File.Delete(parquetFilePath);
}
```
**Convention to follow for a Phase-4 mzPeak test:**
- `[TestFixture]` class, `[Test]` methods, NUnit `Assert.That(...)` constraint style.
- Input fixture: `Data/small.RAW` resolved via `AppDomain.CurrentDomain.BaseDirectory`; output to `Path.GetTempPath()`; clean up with `File.Delete` / `Directory.Delete`.
- Construct `new ParseInput(testRawFile, null, tempFilePath, OutputFormat.MzPeak)` and drive via `RawFileParser.Parse(parseInput)` (full integration, not the writer in isolation).
- Assert `parseInput.Errors == 0` and `Warnings == 0`, then `File.Exists("small.mzpeak")`.
- **Change for mzPeak:** open the produced `.mzpeak` as a `ZipArchive`, read the Parquet entry with `ParquetReader` (as above) and/or assert `mzpeak_index.json` is present. The CONTEXT success gate ("reference Python reader opens it") is an out-of-process check — that belongs in a script/CI step, not necessarily this NUnit test; the NUnit test should at minimum confirm a STORED ZIP with the expected entries.
- Test project excludes itself from the main build (`csproj` lines 31-33) and references NUnit + Parquet directly (WriterTests `using NUnit.Framework; using Parquet;`).

---

## Metadata

**Analog search scope:** `Writer/`, `Util/`, root (`OutputFormat.cs`, `RawFileParser.cs`, `MainClass.cs`, `ParseInput.cs`), `ThermoRawFileParserTest/`.
**Files read in full or in part:** `OutputFormat.cs`, `RawFileParser.cs`, `Writer/ParquetSpectrumWriter.cs`, `Writer/SpectrumWriter.cs`, `Writer/MgfSpectrumWriter.cs` (head), `Writer/WriterUtil.cs`, `Writer/OntologyMapping.cs` (head), `Util/CVHelpers.cs`, `Util/GeneralHelpers.cs`, `ParseInput.cs`, `MainClass.cs` (format option + ParseToEnum), `ThermoRawFileParserTest/WriterTests.cs`, `ThermoRawFileParser.csproj`, phase `CONTEXT.md`.
**Pattern extraction date:** 2026-06-14
