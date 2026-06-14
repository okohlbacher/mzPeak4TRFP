# Phase 1 Context: Walking Skeleton — CLI Wiring + Parquet/ZIP Foundation

**Requirements:** CLI-01, CLI-02, CLI-03, PQ-01, PQ-02, PQ-03

## Intent

Prove the entire mzPeak pipeline end-to-end with the thinnest possible slice before any data
fidelity is added. The output need not contain real spectra yet — it must be a STORED ZIP that
contains at least one valid Parquet facet plus `mzpeak_index.json`, and the reference Python
reader must OPEN it without error. The phase exists primarily to retire the single biggest
unknown: whether Parquet.Net v5.0.1 can express the nested column shapes mzPeak requires.

## Decisions (locked for this phase)

- **New writer class:** `Writer/MzPeakSpectrumWriter.cs` extending `SpectrumWriter`, mirroring the
  structure of the existing `Writer/ParquetSpectrumWriter.cs`.
- **Enum + dispatch:** add `MzPeak` to `OutputFormat.cs`; update `--format` help text in
  `MainClass.cs`; add a dispatch case in `RawFileParser.cs`. Output extension `.mzpeak`.
- **Parquet API:** use Parquet.Net's **low-level** `ParquetSchema` / `DataColumn` / writer API,
  NOT the high-level POCO `ParquetSerializer`. The POCO serializer (used by ParquetSpectrumWriter)
  cannot express nested structs + lists-of-structs + parallel nullable top-level columns. The spike
  must confirm the low-level API can, including a round-trip read.
- **ZIP:** `System.IO.Compression.ZipArchive` with `CompressionLevel.NoCompression` (STORED) at the
  archive level; Parquet internal compression = ZSTD.
- **Reusable helpers:** a `PARAM` value-struct builder (`struct<value:struct<integer,float,string,boolean>, accession, name, unit>`)
  and a convention helper for CV-accession-embedded column names (e.g. `MS_1000511_ms_level`).
  These are consumed heavily by Phase 3, so design them now.
- **Minimal facet for the skeleton:** simplest acceptable is a `spectra_data.parquet` with the
  point struct schema (even if written with a tiny placeholder set or the first scan's points) plus
  a hand-built `mzpeak_index.json`. Prefer reusing one real spectrum over fabricating data so the
  skeleton is honest.

## Constraints

- m/z = float64, intensity = float32 (canonical widths) — establish the column types now.
- `large_string` / `large_list` (Arrow 64-bit-offset) appear in the ground truth; confirm whether
  Parquet.Net's `string`/`list` are accepted by the reference reader or whether the large variants
  are required. Resolve via round-trip read against the reference reader, not assumption.
- No comments referencing harness/process phases in the code.
- Keep the writer compact; factor shared Parquet/CV helpers into a small support file rather than
  bloating the writer.

## Open questions for research

1. Exact Parquet.Net v5.0.1 API calls to build a nested struct field, a list-of-struct field, and
   to write parallel top-level columns where rows are null in all-but-one column. Confirm with a
   minimal compilable snippet.
2. Does Parquet.Net support ZSTD compression and large_list/large_string? If not, what is the
   closest compatible encoding the reference reader accepts?
3. How to invoke the reference reader for the OPEN check: the Python reader in `refs/mzPeak/python/`
   (pyarrow available) vs. the Rust `cargo r --example` path. Pick the cheapest reliable gate.
4. Minimal required keys in `mzpeak_index.json` for the reader to open the archive (vs. full metadata).

## Reference artifacts

- `refs/_findings/mzpeak_groundtruth_schema.md` — exact target schema.
- `refs/mzPeak/small.unpacked.mzpeak/` — real archive to diff/compare.
- `refs/mzPeak/python/` — reference Python reader.
- `ThermoRawFileParser/Writer/ParquetSpectrumWriter.cs` — closest existing analog.
- `ThermoRawFileParser/Writer/SpectrumWriter.cs` — base class + data extraction.

## Success criteria (from ROADMAP)

1. `-f mzpeak` accepted, listed in help, output `.mzpeak`.
2. Dispatch instantiates `MzPeakSpectrumWriter`; conversion completes without error.
3. Spike proves Parquet.Net nested struct + list-of-structs + parallel nullable top-level columns, round-trip read confirms values.
4. Reusable PARAM value-struct + CV-column-name helpers exist.
5. Archive is STORED ZIP w/ ZSTD-internal Parquet; reference Python reader opens it.
