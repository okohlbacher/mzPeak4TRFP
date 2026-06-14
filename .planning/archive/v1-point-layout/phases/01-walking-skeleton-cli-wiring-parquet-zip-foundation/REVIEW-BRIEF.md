# Adversarial plan review brief — Phase 1 (mzPeak writer for ThermoRawFileParser)

You are an adversarial reviewer. Your job is to find every way the plan below could FAIL,
produce an INVALID mzPeak file, waste effort, or introduce bloat. Be skeptical and specific.
Do NOT modify any files — this is a read-only review. Output findings only.

## What is being built

A new `mzpeak` output format for the C#/.NET ThermoRawFileParser (TRFP). Phase 1 is a
walking skeleton: wire `-f mzpeak` end-to-end and emit the thinnest archive the reference
Python mzPeak reader can OPEN — a STORED ZIP of ZSTD-internal Parquet facets + mzpeak_index.json.

## Read these files (relative to repo root /Users/kohlbach/Claude/mzPeak4TRFR)

- .planning/phases/01-walking-skeleton-cli-wiring-parquet-zip-foundation/01-01-PLAN.md   ← THE PLAN under review
- .planning/phases/01-walking-skeleton-cli-wiring-parquet-zip-foundation/RESEARCH.md      ← resolved unknowns + proven spike
- .planning/phases/01-walking-skeleton-cli-wiring-parquet-zip-foundation/CONTEXT.md
- .planning/ROADMAP.md (Phase 1 section), .planning/REQUIREMENTS.md (CLI-01..03, PQ-01..03)
- refs/_findings/mzpeak_groundtruth_schema.md  ← exact target schema
- ThermoRawFileParser/Writer/ParquetSpectrumWriter.cs, ThermoRawFileParser/Writer/SpectrumWriter.cs (analogs)
- ThermoRawFileParser/OutputFormat.cs, RawFileParser.cs, MainClass.cs (wiring points)

## Evaluate specifically

1. CORRECTNESS: Will the three tasks, as written, actually compile and run on this codebase?
   Are the cited file:line wiring points and the SpectrumWriter/ParquetSpectrumWriter ctor and
   ConfigureWriter contracts accurate? Any enum-ordinal / ParseToEnum / help-text breakage?
2. PARQUET.NET: Is the low-level API usage (ParquetSchema/StructField/ListField/DataColumn,
   GetDataFields() leaf order, definition-level null handling, ZSTD, MemoryStream-then-zip) correct
   and sufficient for nested struct + list-of-struct + parallel nullable top-level structs?
   Any wrong assumption that would only surface at runtime?
3. SPEC CONFORMANCE: Will the produced archive actually OPEN in the reference reader? Is the claim
   that BOTH spectra_data and spectra_metadata facets are required correct? Are the minimal
   mzpeak_index.json + Parquet CustomMetadata keys (spectrum_count, spectrum_data_point_count,
   spectrum_array_index, spectrum.index leaf) sufficient and accurate? STORED-zip / ZSTD details right?
4. SCOPE & BLOAT: Is anything over-engineered for a walking skeleton, or under-specified such that the
   executor will guess wrong? Are the PARAM/CV helper APIs (consumed by Phase 3) well-shaped or premature?
5. RISKS/GAPS: Build runtime mismatch (net8 target vs net9/10), Python reader env, zero-point spectra,
   honesty of "one real spectrum" data, test conventions. Anything missing that blocks Definition of Done?
6. The repo constraint: code must be compact and contain NO comments referencing harness/process/phases.
   Flag any plan instruction that would violate this.

## Output format

Return a prioritized list of findings. For each: SEVERITY (BLOCKER / HIGH / MEDIUM / LOW / NIT),
the specific location (task #, file, or plan section), the concrete problem, and a concrete fix.
End with a one-line VERDICT: SHIP / SHIP-WITH-FIXES / REWORK. Be concise; no preamble.
