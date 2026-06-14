# Phase 1: Walking Skeleton — CLI Wiring + Parquet/ZIP Foundation - Research

**Researched:** 2026-06-14
**Domain:** Parquet.Net v5.0.1 low-level nested-schema writing; mzPeak archive format; TRFP CLI wiring
**Confidence:** HIGH (the make-or-break unknown was retired by a compiled spike + round-trip read against the reference reader, not by assumption)

## Summary

The single make-or-break unknown — **can Parquet.Net v5.0.1 express mzPeak's nested column
shapes?** — is now **RETIRED with HIGH confidence**. A compiled spike against the actually-installed
`Parquet.Net 5.0.1` (net8.0 lib) wrote a Parquet file containing a nested struct, a list-of-struct
(with empty lists and inner nulls), and two **parallel nullable top-level structs** where each row
populated exactly one and the other was null. pyarrow read it back with the exact intended shapes and
values. ZSTD page compression and string→string KV metadata both work. A minimal STORED-zip archive
(spectra_data + spectra_metadata + mzpeak_index.json) was then built and **opened successfully by the
reference Python reader** (`MzPeakFile`), confirming that Parquet.Net's plain `string`/`list` (32-bit
offset) variants are accepted where the ground truth uses `large_string`/`large_list`.

Two non-obvious API constraints were discovered and must shape the writer design: (1) columns must be
written **in exact schema leaf order** (`GetDataFields()` order), and (2) when supplying explicit
definition levels, the data array carries **only the defined (non-null) values** as the
non-nullable element type — nulls are conveyed purely through definition levels. The
packed-parallel-null pattern is implemented by giving each parallel top-level struct's leaves a
definition level of 0 on rows where that struct is absent.

The reference reader's "OPEN" is **not** satisfied by any arbitrary Parquet: the spectrum-metadata
facet requires KV-metadata keys `spectrum_count` and `spectrum_data_point_count` plus a `spectrum`
struct with an `index` leaf; the spectrum-data facet requires a `spectrum_array_index` JSON KV entry.
These minimums are documented below.

**Primary recommendation:** Use `ParquetWriter.CreateAsync` + `StructField`/`ListField`/`DataField<T>`
schema + `ParquetRowGroupWriter.WriteColumnAsync(DataColumn)` with manual definition/repetition levels;
set `writer.CompressionMethod = CompressionMethod.Zstd` and `writer.CustomMetadata`; emit plain
string/list (no large variants needed); package facets into a STORED `ZipArchive`
(`CompressionLevel.NoCompression`).

## Architectural Responsibility Map

| Capability | Primary Tier | Secondary Tier | Rationale |
|------------|-------------|----------------|-----------|
| CLI flag parse / dispatch | TRFP `MainClass`/`RawFileParser` | — | Existing format-routing infrastructure |
| Spectrum data extraction | TRFP `SpectrumWriter` base (`ReadMZData`, `ReadScan`) | — | Already implemented and reused by every writer |
| Parquet schema + column encoding | New `MzPeakSpectrumWriter` + shared Parquet helper | Parquet.Net | Low-level nested encoding the POCO serializer can't do |
| PARAM / CV-column-name helpers | Shared support file | — | Consumed heavily by Phase 3; design now |
| Archive packaging (STORED zip) | New `MzPeakSpectrumWriter` | `System.IO.Compression` | Format-level container, BCL-native |
| KV metadata + array_index JSON | New writer / helper | Newtonsoft.Json (already a dep) | Reader OPEN depends on these keys |

## Standard Stack

### Core
| Library | Version | Purpose | Why Standard |
|---------|---------|---------|--------------|
| Parquet.Net | 5.0.1 | Low-level Parquet schema + column writing | Already a TRFP dep [VERIFIED: csproj line 44]; low-level API proven sufficient by spike |
| System.IO.Compression | net8.0 BCL | STORED ZIP container | BCL-native, no new dep |
| Newtonsoft.Json | 13.0.3 | Build `mzpeak_index.json` + KV-metadata JSON | Already a TRFP dep [VERIFIED: csproj line 42] |

No new NuGet packages are required for Phase 1. **Package Legitimacy Audit is N/A** (no external packages installed).

### Parquet.Net 5.0.1 verified API surface (reflected from installed DLL)
[VERIFIED: ~/.nuget/packages/parquet.net/5.0.1/lib/net8.0/Parquet.dll]

```
Parquet.Schema.ParquetSchema(params Field[])            // or IEnumerable<Field>
  DataField[] GetDataFields()                           // leaf fields, IN ORDER
Parquet.Schema.StructField(string name, params Field[] elements)
Parquet.Schema.ListField(string name, Field item)      // item may be a StructField
Parquet.Schema.DataField<T>(string name, bool? nullable = null)
Parquet.Data.DataColumn(DataField field, Array data)                                  // flat, no nulls
Parquet.Data.DataColumn(DataField field, Array definedData, int[] definitionLevels, int[] repetitionLevels)
Parquet.ParquetWriter.CreateAsync(ParquetSchema schema, Stream output, ...) : Task<ParquetWriter>
  ParquetWriter.CompressionMethod { get; set; }         // enum, settable
  ParquetWriter.CustomMetadata    { get; set; }          // IReadOnlyDictionary<string,string>, settable
  ParquetWriter.CreateRowGroup() : ParquetRowGroupWriter
ParquetRowGroupWriter.WriteColumnAsync(DataColumn) : Task
Parquet.CompressionMethod = { None, Snappy, Gzip, Lzo, Brotli, LZ4, Zstd, Lz4Raw }
```

Note: `Parquet.Serialization.ParquetSerializer` (high-level POCO) still exists but is NOT used here —
it cannot express parallel nullable top-level structs (confirmed by CONTEXT.md decision).

---

## Unknown 1 — Parquet.Net nested shapes — DECISION: RESOLVED (the low-level API handles all three)

**Spike result (compiled + round-tripped):** Parquet.Net 5.0.1 successfully writes and pyarrow reads
back: nested struct, list-of-struct (with empty list + inner null), and parallel nullable top-level
structs (per-row exactly-one-populated). Output below is the literal pyarrow read of the spike file.

```
spectrum: struct<index: uint64, mz_range: struct<lo,hi>, parameters: list<struct<name:string, val:double>>>
scan:     struct<source_index: uint64, rt: float>

spectrum row values: [
  {index:10, mz_range:{lo:100.5, hi:2000.0}, parameters:[{name:'a',val:1.5},{name:'b',val:None}]},
  None,                                                  # row1: spectrum NULL
  {index:11, mz_range:{lo:50.0, hi:60.0}, parameters:[]} # row2: empty list
]
scan row values:    [ None, {source_index:10, rt:1.23}, None ]   # only row1 populated
```

### (a) nested StructField
```csharp
var mzStruct = new StructField("mz_range",
    new DataField<double>("lo"),
    new DataField<double>("hi"));
```

### (b) ListField whose item is a StructField (list-of-struct)
```csharp
var paramItem = new StructField("item",
    new DataField<string>("name"),
    new DataField<double?>("val"));
var paramsList = new ListField("parameters", paramItem);   // -> list<struct<name,val>>
```

### (c) parallel nullable top-level structs
```csharp
var spectrum = new StructField("spectrum",
    new DataField<ulong>("index"), mzStruct, paramsList);
var scan = new StructField("scan",
    new DataField<ulong>("source_index"), new DataField<float>("rt"));
var schema = new ParquetSchema(spectrum, scan);   // two parallel top-level structs
```

### Writing — the two API rules that bite

**Rule 1 — write columns in schema leaf order.** `GetDataFields()` returns leaves in a fixed order;
`WriteColumnAsync` rejects out-of-order columns (`ArgumentException: cannot write this column,
expected 'integer', passed: 'name'`). Always iterate `GetDataFields()` and write in that order.

**Rule 2 — defined data + definition levels (NOT nullable arrays).** When you pass explicit
definition levels, the data array must contain only the **present** values, typed as the
non-nullable element type. Passing a `double?[]` with embedded nulls throws
(`expected System.Double[] but passed System.Nullable...`). Express nulls via def levels only.

**Definition levels are auto-derived from struct nesting.** Parquet.Net makes top-level struct
fields implicitly nullable: a leaf directly under one nullable struct has `MaxDefinitionLevel = 1`
(1 = struct present, 0 = struct null). A leaf under a struct-within-a-struct has `MaxDefinitionLevel = 2`,
and so on. Verified leaf levels for the spike schema:

```
spectrum/index                         maxDef=1  maxRep=0   (1=spectrum present)
spectrum/mz_range/lo                    maxDef=2  maxRep=0   (2=spectrum+mz_range present)
spectrum/parameters/list/item/name     maxDef=5  maxRep=1   (list-of-struct: def 5 / rep 1)
scan/source_index                      maxDef=1  maxRep=0
```

**Canonical write loop (the packed-parallel-null pattern):**
```csharp
using var writer = await ParquetWriter.CreateAsync(schema, outStream);
writer.CompressionMethod = CompressionMethod.Zstd;
writer.CustomMetadata = new Dictionary<string,string> {
    ["spectrum_count"] = nSpectra.ToString(),
    ["spectrum_data_point_count"] = "0",                  // metadata facet uses 0
};
using var rg = writer.CreateRowGroup();

DataField F(string path) =>
    schema.GetDataFields().First(d => d.Path.ToString() == path);  // Path uses '/' separators

// 3 rows: row0=spectrum, row1=scan, row2=spectrum(empty params)
// spectrum.index present on rows 0,2 (def=1), absent on row1 (def=0):
await rg.WriteColumnAsync(new DataColumn(F("spectrum/index"),
    new ulong[]{10, 11}, new int[]{1,0,1}, null));         // rep=null for non-repeated leaves
await rg.WriteColumnAsync(new DataColumn(F("spectrum/mz_range/lo"),
    new double[]{100.5, 50.0}, new int[]{2,0,2}, null));
// list-of-struct leaf: 2 items in row0, struct absent row1 (def0), empty list row2 (def2):
await rg.WriteColumnAsync(new DataColumn(F("spectrum/parameters/list/item/name"),
    new string[]{"a","b"}, new int[]{5,5,0,2}, new int[]{0,1,0,0}));
await rg.WriteColumnAsync(new DataColumn(F("spectrum/parameters/list/item/val"),
    new double[]{1.5}, new int[]{5,4,0,2}, new int[]{0,1,0,0}));  // val null at row0/item1 = def4
// scan struct present only on row1:
await rg.WriteColumnAsync(new DataColumn(F("scan/source_index"),
    new ulong[]{10}, new int[]{0,1,0}, null));
```

**Definition-level cheat sheet for list-of-struct (`parameters` under a nullable struct):**
- `0` = parent struct null (no list)
- `2` = struct present, list present but empty (one entry per row, rep=0)
- `5` = item present, leaf value non-null
- `4` = item present, leaf value null (e.g. nullable `val`/`accession`/`unit`)

**Fallback (not needed):** none. The low-level API expresses every required shape. No
manual Arrow IPC or external tooling is required.

---

## Unknown 2 — Compression + Arrow large variants — DECISION: ZSTD via enum; plain string/list, large NOT required

**ZSTD:** [VERIFIED: spike round-trip] `Parquet.CompressionMethod.Zstd` exists and works.
Set `writer.CompressionMethod = CompressionMethod.Zstd;` before creating the row group. pyarrow
reported every column compression as `ZSTD` in the spike output. This matches the ground-truth files,
whose every column is ZSTD [VERIFIED: pyarrow inspection of small.unpacked.mzpeak].

**Large variants:** [VERIFIED: spike inspection] Parquet.Net 5.0.1 emits **plain `string` and `list`
(32-bit offsets)** — it does NOT emit Arrow `large_string`/`large_list`. There is no API option in the
low-level path to force the large variants.

**Are large variants mandatory?** **No.** [VERIFIED: reference reader OPEN] A minimal archive whose
metadata facet used plain `string` (`id`) and plain `list` (`parameters`) was opened by the reference
Python reader and the `spectrum` table parsed correctly (`id='index=0'`, `parameters=[]`). The reader
uses pyarrow which transparently accepts both 32-bit and 64-bit offset variants. **Decision: emit
whatever Parquet.Net produces (plain string/list); do not attempt to force large variants.**

> Caveat (LOW risk): plain `string`/`list` use 32-bit offsets, capping a single column chunk's
> cumulative string/list bytes at 2 GiB. For TRFP per-RAW outputs this is not a practical limit, and
> row-group chunking keeps each chunk small. Note for Phase 3/4 if extremely large metadata columns
> ever appear (they won't for spectra metadata).

---

## Unknown 3 — Reference reader OPEN gate — DECISION: Python `MzPeakFile(...)` one-liner, exit code = gate

**Cheapest reliable OPEN gate** (self-contained, no Rust build). [VERIFIED: run against both the
minimal spike archive and the real reference archive]

```bash
PYTHONPATH=refs/mzPeak/python python3.11 -c "
import numpy, numpy.typing
from mzpeak import MzPeakFile
f = MzPeakFile('OUTPUT.mzpeak')
assert f.spectrum_metadata is not None and len(f.spectrum_metadata) >= 1
print('OPEN OK', len(f.spectrum_metadata))
"
```
- Exit 0 = archive opened and the spectrum-metadata facet parsed.
- Nonzero = failure (raises before the `print`).

**Verification runs:**
- Minimal spike archive → `OPEN OK 1`, exit 0.
- Real `refs/mzPeak/small.mzpeak` (control) → `OPEN OK 48`, exit 0.

**Environment requirements for the gate** (the system anaconda python3 is 3.7 and cannot run the
`match`/`StrEnum` reader; use `python3.11`):
- Python ≥ 3.11 (reader uses `match` statements and `StrEnum`).
- `pyarrow`, `pandas`, `numpy` — present in the `python3.11` env [VERIFIED].
- `pynumpress` and `psims` — hard top-level imports in the reader; installed via
  `python3.11 -m pip install pynumpress psims` [VERIFIED: both installed and OPEN passed].
- `import numpy.typing` must be forced before importing mzpeak (older numpy build doesn't auto-load
  the `typing` submodule) — included in the one-liner above. [VERIFIED]

**Rust alternative (not recommended for the gate):** `cd refs/mzPeak && cargo r --example read -- OUTPUT.mzpeak`
opens via `ArchiveReader`/`MzPeakReader` but requires a full Rust build of the workspace and is slower
to invoke. Prefer the Python path. (For an even cheaper *structural* pre-check that needs no mzpeak
deps: `python3 -c "import zipfile,json; z=zipfile.ZipFile('OUTPUT.mzpeak'); assert all(i.compress_type==0 for i in z.infolist()); json.load(z.open('mzpeak_index.json'))"` — but this only checks STORED+index, not reader acceptance.)

---

## Unknown 4 — mzpeak_index.json minimum — DECISION: 2 file entries, empty metadata block

[VERIFIED: schema/mzpeak_index.json requires only `files` + `metadata`; reader `_from_zip_archive`
keys on `entity_type`/`data_kind`; OPEN succeeded with the JSON below]

The JSON schema (`refs/mzPeak/schema/mzpeak_index.json`) requires exactly two top-level keys:
`files` (array) and `metadata` (object, `additionalProperties: true` → **may be `{}`**). Each file
entry needs `name`, `entity_type`, `data_kind`. The reader matches on the `(EntityType, DataKind)`
tuple to decide which facet reader to construct (`reader.py` `_from_zip_archive` / `file_index.py`).

**Minimal index for OPEN** (the metadata block may be empty for the skeleton):
```json
{
  "files": [
    { "name": "spectra_data.parquet",     "entity_type": "spectrum", "data_kind": "data arrays" },
    { "name": "spectra_metadata.parquet", "entity_type": "spectrum", "data_kind": "metadata" }
  ],
  "metadata": {}
}
```

**Reader OPEN actually requires** (beyond the index file itself):
- `mzpeak_index.json` MUST exist in the zip, else `FileNotFoundError` (reader `_from_zip_archive`).
- A `(spectrum, metadata)` facet — the reader builds `MzPeakSpectrumMetadataReader`, which reads
  KV-metadata keys `spectrum_count` and `spectrum_data_point_count` from that Parquet (both required,
  else `IndexError`/`KeyError`). The `spectrum` struct must have an `index` leaf
  (`path_in_schema == "spectrum.index"`). [VERIFIED]
- A `(spectrum, data arrays)` facet — builds `MzPeakArrayDataReader`, which reads the
  `spectrum_array_index` KV-metadata JSON (`{"prefix":"point","entries":[...]}`) and `spectrum_count`.
  [VERIFIED: spike supplied this and OPEN passed]
- Valid `entity_type`/`data_kind` strings: `spectrum`/`chromatogram` and
  `data arrays`/`metadata`/`peaks` (exact lowercase strings) [VERIFIED: file_index.py StrEnums].

> Insight: only **list the facets you actually write**. Listing a facet in `files[]` that doesn't
> exist (or lacks its required KV keys) makes the reader try to construct that facet reader and fail.
> For the skeleton, write exactly the two facets above and list exactly those two.

**KV metadata keys the reader needs** (set via `writer.CustomMetadata`):

| Facet | Required KV keys | Spike value used |
|-------|------------------|------------------|
| spectra_metadata.parquet | `spectrum_count`, `spectrum_data_point_count` | "1", "3" |
| spectra_data.parquet | `spectrum_array_index` (JSON), `spectrum_count`, `spectrum_data_point_count` | see below |

`spectrum_array_index` minimal value that opened (entries describe the m/z + intensity arrays):
```json
{"prefix":"point","entries":[
  {"context":"spectrum","path":"point.mz","data_type":"MS:1000523","array_type":"MS:1000514",
   "array_name":"m/z array","unit":"MS:1000040","buffer_format":"point","buffer_priority":"primary"},
  {"context":"spectrum","path":"point.intensity","data_type":"MS:1000521","array_type":"MS:1000515",
   "array_name":"intensity array","unit":"MS:1000131","buffer_format":"point","buffer_priority":"primary"}
]}
```

---

## Unknown 5 — ZIP STORED — DECISION: ZipArchive + CompressionLevel.NoCompression (verified STORED)

[VERIFIED: spike zip inspected — all entries `compress_type=0` (STORED); reference reader opened it]

```csharp
using var zip = ZipFile.Open(outputPath, ZipArchiveMode.Create);
void AddStored(string name, byte[] bytes) {
    var entry = zip.CreateEntry(name, CompressionLevel.NoCompression);  // -> STORED
    using var s = entry.Open();
    s.Write(bytes, 0, bytes.Length);
}
AddStored("mzpeak_index.json", indexJsonBytes);
AddStored("spectra_data.parquet", dataParquetBytes);
AddStored("spectra_metadata.parquet", metaParquetBytes);
```

**Gotcha confirmed false here:** in .NET 8, `CompressionLevel.NoCompression` does produce a true
STORED entry (method 0), not a deflate-with-level-0 entry. The spike's three entries all reported
`compress_type=0` via Python `zipfile`. [VERIFIED]

**Reference reader treats the zip as a directory-of-parquet:** `reader.py` opens the zip with
`zipfile.ZipFile`, reads `mzpeak_index.json`, then `archive.open(name)` each listed parquet and wraps
it in `pa.PythonFile`. It also supports an unpacked directory (`small.unpacked.mzpeak/`). [VERIFIED:
reader.py `_from_zip_archive` / `_from_directory`]

> Practical note: build each Parquet facet fully in a `MemoryStream` first (Parquet.Net writes its
> footer on dispose / needs a seekable stream), then write the finished bytes into the STORED zip
> entry. Do not hand Parquet.Net the zip entry stream directly (zip entry streams are not seekable).

---

## Unknown 6 — TRFP wiring specifics — DECISION: exact insertion points confirmed

All file:line references [VERIFIED: read from source].

**Writer constructor signature the new writer must match** (all existing writers use the identical
one-arg ctor delegating to the base):
```csharp
public MzPeakSpectrumWriter(ParseInput parseInput) : base(parseInput) { }
```
- `MgfSpectrumWriter(ParseInput)`  — `Writer/MgfSpectrumWriter.cs:20`
- `MzMlSpectrumWriter(ParseInput)` — `Writer/MzMlSpectrumWriter.cs:55`
- `ParquetSpectrumWriter(ParseInput)` — `Writer/ParquetSpectrumWriter.cs:43`
- base: `protected SpectrumWriter(ParseInput parseInput)` — `Writer/SpectrumWriter.cs:63`
- abstract method to override: `public abstract void Write(IRawDataPlus rawFile, int firstScanNumber, int lastScanNumber)` — `Writer/SpectrumWriter.cs:72`

**1. Enum value** — `OutputFormat.cs` (current enum: `MGF, MzML, IndexMzML, Parquet, None`).
Insert `MzPeak` **before `None`** to keep `None`'s ordinal meaning stable in help text. New ordinal: 4.
```csharp
public enum OutputFormat { MGF, MzML, IndexMzML, Parquet, MzPeak, None }
```
Numbering ripple: `None` moves from 4 → 5. Help text below must reflect this.

**2. Help text** — `MainClass.cs:530-531` (`"f=|format="` option description). Current:
> "The spectra output format: 0 for MGF, 1 for mzML, 2 for indexed mzML, 3 for Parquet, 4 for None ...".
Update to include `4 for mzPeak, 5 for None`. (Format string is text/numeric tolerant; `ParseToEnum`
at `MainClass.cs:911` handles both name and int.)

**3. Dispatch switch** — `RawFileParser.cs:169-184` (`switch (parseInput.OutputFormat)`).
Add a case mirroring the Parquet case (lines 180-183):
```csharp
case OutputFormat.MzPeak:
    spectrumWriter = new MzPeakSpectrumWriter(parseInput);
    spectrumWriter.Write(rawFile, firstScanNumber, lastScanNumber);
    break;
```

**4. Gzip suppression** — `MainClass.cs:776` currently forces `Gzip=false` for Parquet. Extend to
also cover MzPeak (a STORED zip must never be gzip-wrapped):
```csharp
if (parseInput.OutputFormat == OutputFormat.Parquet ||
    parseInput.OutputFormat == OutputFormat.MzPeak) parseInput.Gzip = false;
```

**5. Output stream / extension** — `SpectrumWriter.ConfigureWriter` (`Writer/SpectrumWriter.cs:78-103`)
has a `OutputFormat.Parquet` branch (line 88) that creates a plain binary `File.Create` stream (no
gzip, no text). Extend that condition to include `MzPeak`, then call `ConfigureWriter(".mzpeak")` from
the new writer to get `Writer.BaseStream`, and write the finished STORED-zip bytes there (mirrors how
`ParquetSpectrumWriter` writes to `Writer.BaseStream`). `NormalizeFileName` already yields the right
`<rawname>.mzpeak` output path. Alternatively the writer can open its own `ZipFile` on the target path;
either is acceptable, but reusing `ConfigureWriter` keeps stdout/StreamWriter lifecycle consistent.

> Watch-out: `ConfigureWriter` wraps the stream in a `StreamWriter`. For binary zip output, write to
> `Writer.BaseStream` (as ParquetSpectrumWriter does), then `Writer.Flush()`/`Writer.Close()`.

---

## Reusable helpers to design now (consumed by Phase 3)

Per CONTEXT.md "Reusable helpers" decision. Factor into a small support file (e.g.
`Writer/MzPeak/MzPeakSchema.cs`), not the writer:

1. **PARAM value-struct builder** — the repeated nested type
   `struct<value: struct<integer:int64, float:double, string:large_string→string, boolean:bool>,
   accession:string, name:string, unit:string>`. [VERIFIED: exact shape from ground-truth pyarrow
   dump]. Provide: a `StructField BuildParamField(string name)` factory **and** a column-writer that,
   given a list of params per row, emits all 7 leaves with correct def/rep levels in schema order.
   Leaf order (must match): `value/integer, value/float, value/string, value/boolean, accession, name, unit`.

2. **CV-accession column-name helper** — convention `MS_<acc>_<label>[_unit_<NS>_<acc>]`
   (e.g. `MS_1000511_ms_level`, `MS_1000016_scan_start_time_unit_UO_0000031`). [VERIFIED: ground-truth
   column names]. The reference reader's `OntologyMapper.clean_column_names` strips this back to human
   labels at read time, so the embedded accession is how CV terms travel — get the convention right now.

---

## Architecture Patterns

### Data flow (Phase 1 skeleton)
```
RAW file ──(TRFP RawFileReader)──> SpectrumWriter.ReadScan/ReadMZData (existing, reused)
                                          │ first/placeholder spectrum points
                                          ▼
                         MzPeakSpectrumWriter.Write()
                                          │
            ┌─────────────────────────────┴─────────────────────────────┐
            ▼                                                             ▼
   build spectra_data.parquet (MemoryStream)              build spectra_metadata.parquet (MemoryStream)
   schema: point<spectrum_index,mz,intensity>            schema: spectrum<index,id,ms_level,time,
   KV: spectrum_count, spectrum_data_point_count,                 number_of_data_points, TIC, parameters>
       spectrum_array_index                              KV: spectrum_count, spectrum_data_point_count
   ZSTD                                                  ZSTD
            └─────────────────────────────┬─────────────────────────────┘
                                          ▼
                 ZipArchive (STORED) <── mzpeak_index.json + both parquet byte[]
                                          ▼
                                  <rawname>.mzpeak
                                          ▼
                 OPEN gate: MzPeakFile('<rawname>.mzpeak') exit 0
```

### Component responsibilities
| Component | File | Responsibility |
|-----------|------|----------------|
| Enum + dispatch | `OutputFormat.cs`, `RawFileParser.cs`, `MainClass.cs` | Route `-f mzpeak` to the new writer |
| `MzPeakSpectrumWriter` | `Writer/MzPeakSpectrumWriter.cs` | Orchestrate: read scans → build facets → STORED zip |
| Parquet/CV helpers | `Writer/MzPeak/*.cs` (support file) | PARAM field + CV column-name conventions, reused Phase 3 |

### Anti-Patterns to Avoid
- **Using `ParquetSerializer` (POCO):** cannot express parallel nullable top-level structs. Use the
  low-level schema/DataColumn API.
- **Embedding nulls in the data array when supplying def levels:** throws. Pass only defined values.
- **Writing columns out of `GetDataFields()` order:** throws.
- **Handing Parquet.Net a non-seekable stream (e.g. a zip entry):** build in `MemoryStream` first.
- **Listing a facet in `mzpeak_index.json` you didn't fully write (missing KV keys):** reader fails OPEN.
- **gzip-wrapping the `.mzpeak`:** must be a raw STORED zip (suppress `Gzip`).

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Parquet encoding | Custom column/page encoder | Parquet.Net low-level API | Def/rep levels, ZSTD, footer all handled |
| ZIP STORED container | Manual zip byte layout | `System.IO.Compression.ZipArchive` | STORED verified, BCL-native |
| JSON index/KV | String concatenation | Newtonsoft.Json (existing dep) | Escaping, nested objects |
| Scan/precursor extraction | New RAW reading | `SpectrumWriter` base methods | `ReadScan`/`ReadMZData` already battle-tested |

## Common Pitfalls

### Pitfall 1: Column write order / nullable-array confusion
**What goes wrong:** `ArgumentException` on `WriteColumnAsync`. **Why:** columns must follow
`GetDataFields()` order, and def-level columns take non-nullable defined-only data. **Avoid:** iterate
`GetDataFields()`; build `(definedValues, defLevels, repLevels)` triples. **Warning sign:** "expected
'X' passed 'Y'" or "expected System.Double[] but passed System.Nullable".

### Pitfall 2: Reader OPEN fails despite a valid Parquet
**What goes wrong:** `KeyError`/`IndexError` constructing the metadata reader. **Why:** missing KV keys
`spectrum_count` / `spectrum_data_point_count` / `spectrum_array_index`, or a listed facet that wasn't
written. **Avoid:** set all required `CustomMetadata` keys; list only facets you write. **Warning
sign:** traceback inside `MzPeakSpectrumMetadataReader.__init__` or `_DataIndex._infer_schema_idx`.

### Pitfall 3: OPEN gate environment
**What goes wrong:** `SyntaxError: invalid syntax` at `match`. **Why:** system python is < 3.10.
**Avoid:** run the gate with `python3.11`; ensure `pyarrow/pandas/numpy/pynumpress/psims` installed and
force `import numpy.typing`. **Warning sign:** the syntax error or `ImportError: ... psims`.

## State of the Art

| Old Approach | Current Approach | Impact |
|--------------|------------------|--------|
| `ParquetSerializer.SerializeAsync` over flat POCO (existing ParquetSpectrumWriter) | Low-level `ParquetSchema`/`DataColumn` with manual levels | Required for nested/parallel mzPeak shapes |
| Assume large_string/list mandatory | Plain string/list accepted by reader | No need to chase Arrow large variants |

## Assumptions Log

| # | Claim | Section | Risk if Wrong |
|---|-------|---------|---------------|
| A1 | The skeleton may write a single (first/placeholder) spectrum's points and still be "honest" per CONTEXT.md | Unknown 4 / data flow | Low — CONTEXT.md explicitly permits reusing one real spectrum |
| A2 | Reusing `ConfigureWriter(".mzpeak")` for the output stream is acceptable vs. opening `ZipFile` on the path directly | Unknown 6 | Low — both produce the same file; either is fine |
| A3 | Reference reader's pyarrow transparently accepting plain string/list will continue to hold for the Rust reader (Phase 4) | Unknown 2 | Low-Med — Phase 4 should also OPEN with `cargo --example read` to confirm Rust path; pyarrow is the Phase 1 gate |

## Open Questions

1. **Does the Rust reference reader also accept plain string/list?**
   - Known: pyarrow reader OPENs them fine (verified). Rust uses `parquet`/`arrow` crates which also
     accept both offset widths in general.
   - Recommendation: defer to Phase 4's conformance round-trip; Phase 1 gate is the Python reader only.
2. **Exact `spectrum_array_index` fields the Rust reader requires** (Phase 2+ when real data flows).
   - The Python reader accepted the minimal entries above; richer fields (`transform`, `sorting_rank`)
     are present in ground truth. Recommendation: copy the ground-truth entry shape verbatim in Phase 2.

## Environment Availability

| Dependency | Required By | Available | Version | Fallback |
|------------|------------|-----------|---------|----------|
| dotnet SDK | Build TRFP + spike | ✓ | 10.0.301 | — |
| .NET 8 runtime | Run net8.0 TRFP | ✗ (8.0 missing; 9.0/10.0 present) | 9.0.17 / 10.0.9 | TRFP targets net8.0 — install `dotnet-runtime 8` OR roll-forward; spike used net9.0 |
| Parquet.Net | Parquet writing | ✓ | 5.0.1 (net8.0 lib restored) | — |
| python3.11 | Reference reader OPEN gate | ✓ | 3.11.3 | python ≥3.10 with pyarrow |
| pyarrow (py3.11) | Reader | ✓ | 24.0.0 | — |
| pandas / numpy (py3.11) | Reader | ✓ | 2.1.3 / 1.26.2 | — |
| pynumpress (py3.11) | Reader top-level import | ✓ (installed during research) | latest | — |
| psims (py3.11) | Reader CV resolution | ✓ (installed during research) | latest | — |
| cargo | Rust reader (optional) | ✓ | 1.96.0 | Python reader is primary gate |

**Missing dependencies with no fallback:** none blocking. **Note:** TRFP `csproj` targets `net8.0`
but the machine has only the 9.0/10.0 runtimes; `dotnet build`/test will require either installing the
.NET 8 runtime or relying on roll-forward. The Parquet spike was run on net9.0 successfully — the
Parquet.Net API is identical. Flag for the planner: confirm the build/test runtime story.

## Validation Architecture

> `.planning/config.json` not present in repo at research time → nyquist_validation treated as enabled.

### Test Framework
| Property | Value |
|----------|-------|
| Framework | NUnit (existing `ThermoRawFileParserTest/`) for C#; pytest available in reference for OPEN |
| Config file | `ThermoRawFileParser/ThermoRawFileParserTest/` (existing test project) |
| Quick run command | `dotnet test ThermoRawFileParser/ThermoRawFileParserTest` (per-runtime caveat above) |
| Reader OPEN command | see Unknown 3 one-liner |

### Phase Requirements → Test Map
| Req | Behavior | Test Type | Command | Exists? |
|-----|----------|-----------|---------|---------|
| CLI-01 | `-f mzpeak` accepted | smoke | run TRFP with `-f mzpeak`, expect exit 0 + `.mzpeak` | ❌ Wave 0 |
| CLI-02 | listed in `--format` help | unit | assert help string contains "mzPeak" | ❌ Wave 0 |
| CLI-03 | dispatch builds `MzPeakSpectrumWriter` | integration | conversion completes without error | ❌ Wave 0 |
| PQ-01 | nested struct + list-of-struct + parallel nullable cols | integration | write + pyarrow/reader read-back | ✓ proven by spike; formalize as test |
| PQ-02 | STORED zip + ZSTD-internal parquet | unit | zipfile compress_type==0; pyarrow column compression==ZSTD | ❌ Wave 0 |
| PQ-03 | reference reader OPENs archive | integration | `MzPeakFile(out).spectrum_metadata` len ≥ 1, exit 0 | ✓ proven; wire into NUnit/CI |

### Sampling Rate
- Per task commit: `dotnet build`; for Parquet tasks, the pyarrow read-back of the produced facet.
- Per wave merge: convert `ThermoRawFileParserTest/Data/small.RAW` (if present) → `.mzpeak` → OPEN gate.
- Phase gate: OPEN gate green against the produced archive before `/gsd:verify-work`.

### Wave 0 Gaps
- [ ] NUnit test: `-f mzpeak` end-to-end produces `.mzpeak`, opens via reference reader (CLI-01/03, PQ-03).
- [ ] NUnit/struct test: produced zip is STORED and parquet is ZSTD (PQ-02).
- [ ] Test fixture: shared helper to invoke the Python OPEN gate from the test (or a structural fallback
      asserting STORED + index + KV keys when the Python env is unavailable).
- [ ] Decide .NET 8 runtime install vs. roll-forward so `dotnet test` runs in this environment.

## Sources

### Primary (HIGH confidence)
- Compiled spike against `~/.nuget/packages/parquet.net/5.0.1/lib/net8.0/Parquet.dll` — reflected API,
  wrote nested/list-of-struct/parallel-struct Parquet, ZSTD, KV metadata; round-tripped via pyarrow.
- `refs/mzPeak/python/mzpeak/{reader.py,mz_reader.py,file_index.py}` — reader OPEN requirements.
- `refs/mzPeak/small.unpacked.mzpeak/*` + pyarrow inspection — ground-truth schema, KV keys, ZSTD.
- `refs/mzPeak/schema/mzpeak_index.json` — index JSON schema (required keys).
- TRFP source: `OutputFormat.cs`, `RawFileParser.cs:166-185`, `MainClass.cs:530-531,776`,
  `Writer/SpectrumWriter.cs:63,72,78-103`, `Writer/*SpectrumWriter.cs` ctors, `csproj:44`.
- Reference reader OPEN executed against minimal spike archive (`OPEN OK 1`) and real
  `small.mzpeak` (`OPEN OK 48`), both exit 0.

### Secondary (MEDIUM confidence)
- `refs/_findings/mzpeak_groundtruth_schema.md` — corroborated against live pyarrow dump.

## Metadata

**Confidence breakdown:**
- Parquet.Net nested capability: HIGH — compiled spike + round-trip read.
- Compression / large variants: HIGH — spike emitted ZSTD plain string/list; reader accepted them.
- Reader OPEN gate: HIGH — executed against minimal + real archives.
- TRFP wiring: HIGH — exact file:line read from source.
- Rust-reader acceptance of plain variants: MEDIUM — deferred to Phase 4.

**Research date:** 2026-06-14
**Valid until:** 2026-07-14 (stable; Parquet.Net pinned at 5.0.1, reference reader vendored in repo)
