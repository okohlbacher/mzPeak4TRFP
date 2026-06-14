1. **BLOCKER — Task 2, `MzPeakParquet.cs`**: The plan says create `static class MzPeakParquet` and `struct MzPeakParam`, which default to `internal`; `MzPeakParquetTests.cs` is in a separate test assembly and will not compile against them. Fix: require `public static class MzPeakParquet` and `public struct MzPeakParam`, or add `InternalsVisibleTo`.

2. **BLOCKER — Task 2, Parquet field lookup**: The plan says look up leaves by `d.Path.ToString()` using `/`-separated paths, but Parquet.Net documents `Field.Path` as dot-separated and the Python reader expects paths like `spectrum.index` and `point.spectrum_index`. Fix: use `FieldPath`/`FindDataField` or dot-separated keys consistently.

3. **HIGH — `build_runtime_setup`**: `DOTNET_ROLL_FORWARD` can help run a built net8 app, but it does not provide the missing net8 reference/targeting pack. This machine only has 9/10 packs. Fix: require installing the .NET 8 SDK/targeting pack for build/test, and reserve roll-forward for execution only.

4. **HIGH — Task 3 ZIP writing**: The plan allows `ZipArchive` on `Writer.BaseStream` and then `Writer.Flush()/Close()`, but default `ZipArchive` disposal closes the base stream; `ZipFile.Open` also needs a path that `ConfigureWriter` keeps private. Fix: choose one path explicitly, preferably `new ZipArchive(Writer.BaseStream, ZipArchiveMode.Create, leaveOpen: true)` after `ConfigureWriter(".mzpeak")`.

5. **HIGH — Task 3 zero-point spectra**: “Write a single honest point from the base peak” is fabrication when the spectrum has zero array points, and base-peak fields may be null. Fix: scan forward to the first in-range spectrum with real points, or prove/write a zero-row data facet with count 0; never synthesize a point.

6. **HIGH — Task 3 no matching scan case**: If MS-level/range filters exclude every scan, the plan does not say whether to emit count 0, fail, or skip output; an executor may still write `spectrum_count=1` with uninitialized data. Fix: define the selected-spectrum-null branch and test it.

7. **MEDIUM — Task 3 metadata KV accuracy**: The plan sets `spectra_metadata.parquet` `spectrum_data_point_count` to the point count, while the research snippet and ground truth metadata facet use `0`; the actual point count belongs on the data facet. Fix: set metadata facet count to `0` or document and test the deliberate deviation.

8. **MEDIUM — PQ-03 verification**: The final gate checks STORED zip and Python OPEN, but does not assert the internal Parquet codec. Since Parquet.Net defaults to Snappy, the archive could pass OPEN while violating PQ-03. Fix: inspect both Parquet entries and assert every column chunk is ZSTD.

9. **MEDIUM — Task 2 `MzPeakParam` nullability**: The planned fields `string String`, `string Accession`, and `string Unit` contradict the PARAM schema where value string/accession/unit are nullable. Fix: use nullable reference fields where the schema permits null and add tests for null accession/unit/string.

10. **MEDIUM — Task 2 `DataColumn` constructor ambiguity**: Parquet.Net XML docs label the two `int[]` parameters in the opposite semantic order from the plan, even if the spike used the plan’s order. Fix: hide raw constructor calls behind a named helper like `Column(field, defined, definitionLevels, repetitionLevels)` and lock it with the round-trip test.

11. **LOW — Task 2 CV helper**: `CvColumn` only specifies CURIE colon replacement; it does not define label normalization, so Phase 3 callers can produce inconsistent column names for labels with spaces, slash, hyphen, or casing. Fix: either require pre-normalized snake_case labels or implement/test normalization in the helper.

12. **LOW — CLI help/docs**: The plan updates `MainClass.cs` help but leaves `ThermoRawFileParser/README.md`’s copied help text stale at “4 for None”. Fix: update README or explicitly state generated docs are out of scope.

VERDICT: REWORK
