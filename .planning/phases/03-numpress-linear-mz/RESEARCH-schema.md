# Phase 3: Numpress-Linear m/z — Reference Schema + Conformance (sub-report)

**Researched:** 2026-06-14
**Scope:** Schema / footer / fixed-point / validator conformance ONLY. The C# MSNumpress port is covered by a separate sub-report.
**Method:** `python3.11` + `pyarrow 24.0.0` + `pynumpress` over `refs/mzPeak/small.numpress.mzpeak` (authoritative Rust-writer output) and `small.chunked.mzpeak` (delta baseline). `mzpeak-validate` (profile mzpeak-0.9) run on both.
**Confidence:** HIGH — every claim below is a direct dump/measurement from the reference artifacts, not training data.

> **Provenance:** All schema/footer/CURIE/byte facts are `[VERIFIED: pyarrow dump of refs/mzPeak/small.numpress.mzpeak]`. The intrinsic m/z error bound is `[VERIFIED: pynumpress decode + 0.5/fp]`. Numpress algorithm semantics are `[CITED: ms-numpress canonical]`.

---

## CRITICAL FINDING — the reference numpress mode also Numpress-encodes intensity (SLOF)

The Phase-3 CONTEXT.md assumes "intensity stays lossless f32 / NOT numpress'd in v2." **The authoritative reference contradicts this.** `small.numpress.mzpeak` was produced with `-c numpress:50 --intensity-numpress-slof` and its chunk struct carries an **8th field `intensity_numpress_slof_bytes`** (transform `MS:1002314`, numpress short-logged-float), with the plain `intensity` list **absent entirely** (no `chunk_secondary` entry in the footer).

This is a scope decision for the planner/discuss-phase, not a settled fact to implement blindly:

- **Option A (match the reference byte-for-byte):** emit `intensity_numpress_slof_bytes` (SLOF, MS:1002314), drop the plain `intensity` list. Intensity becomes **lossy** (SLOF ~ 2-significant-digit log error). This is what drives the headline ~63% size reduction (SLOF roughly halves intensity bytes vs the f32 list).
- **Option B (honor CONTEXT "intensity stays lossless f32"):** numpress ONLY m/z; keep the plain `intensity` list exactly as Phase-2. This is a *valid* archive (the validator does not require intensity be numpress'd) but will NOT be byte-identical to `small.numpress.mzpeak` and will be materially larger than the reference (the m/z numpress saving alone is ~50% of m/z, not 63% overall).

**Recommendation:** flag `[ASSUMED — needs user confirmation]`. The schema-diff verification in CONTEXT ("chunk schema diff vs small.numpress.mzpeak") is only satisfiable under Option A. If the intent is true byte-parity with the reference, intensity SLOF must be in scope. See Assumptions Log A1.

---

## Item 1 — Exact numpress chunk schema

### Decisions

- The numpress chunk struct has **7 children** (vs Phase-2's 6): the Phase-2 six PLUS `mz_numpress_linear_bytes`, and — in the reference — an 8th `intensity_numpress_slof_bytes` **replacing** the plain `intensity` list. `[VERIFIED]`
- `mz_numpress_linear_bytes` IS present: type **`large_list<item: uint8 not null>`** (Arrow large_list; item is `uint8` and **not null**). `[VERIFIED]`
- `mz_chunk_values` is **still present in the schema** (column not dropped) but is **NULL on every numpress row** (488/488 null, 0 non-null, 0 empty). It is NOT absent; it is present-but-all-null. `[VERIFIED]`
- `chunk_encoding` literal value for numpress rows = **`MS:1002312`** (a CURIE — the Numpress-linear term itself, NOT the string "numpress"). All 488 rows carry this single value. Contrast Phase-2 delta where `chunk_encoding == MS:1003089`. `[VERIFIED]`
- The plain `intensity` list field of Phase-2 is **absent** in the reference numpress file; intensity is carried as `intensity_numpress_slof_bytes` (`large_list<uint8 not null>`, transform `MS:1002314`). `[VERIFIED]`

### Authoritative numpress chunk schema (exact pyarrow dump)

```
chunk: struct<
  spectrum_index:                 uint64
  mz_chunk_start:                 double        # transform=MS:1003901, buffer_format=chunk_start, sorting_rank=0
  mz_chunk_end:                   double        # transform=MS:1003901, buffer_format=chunk_end,   sorting_rank=0
  mz_chunk_values:                large_list<item: double>      # transform=MS:1003901, buffer_format=chunk_values; ALL NULL in numpress mode
  chunk_encoding:                 string        # transform=MS:1003901, buffer_format=chunk_encoding; value = "MS:1002312"
  mz_numpress_linear_bytes:       large_list<item: uint8 not null>   # transform=MS:1002312, buffer_format=chunk_transform, sorting_rank=0
  intensity_numpress_slof_bytes:  large_list<item: uint8 not null>   # transform=MS:1002314, buffer_format=chunk_transform, sorting_rank=null  [reference; Option A only]
>
```

Per-child Arrow field metadata (kv pairs attached to each leaf, verbatim from the file):

| child | Arrow type | array_accession | data_type_accession | buffer_format | transform | sorting_rank | unit |
|-------|-----------|-----------------|---------------------|---------------|-----------|--------------|------|
| spectrum_index | uint64 | — | — | — | — | — | — |
| mz_chunk_start | double | MS:1000514 | MS:1000523 | chunk_start | MS:1003901 | 0 | MS:1000040 |
| mz_chunk_end | double | MS:1000514 | MS:1000523 | chunk_end | MS:1003901 | 0 | MS:1000040 |
| mz_chunk_values | large_list\<double\> | MS:1000514 | MS:1000523 | chunk_values | MS:1003901 | 0 | MS:1000040 |
| chunk_encoding | string | MS:1000514 | MS:1000523 | chunk_encoding | MS:1003901 | 0 | MS:1000040 |
| mz_numpress_linear_bytes | large_list\<uint8 not null\> | MS:1000514 | MS:1000523 | chunk_transform | **MS:1002312** | 0 | MS:1000040 |
| intensity_numpress_slof_bytes | large_list\<uint8 not null\> | MS:1000515 | MS:1000521 | chunk_transform | **MS:1002314** | null | MS:1000131 |

### Representative rows (spectrum 0, first 3 chunk rows)

| row | spectrum_index | mz_chunk_start | mz_chunk_end | mz_chunk_values | chunk_encoding | mz_numpress_linear_bytes | intensity_numpress_slof_bytes |
|-----|---------------|----------------|--------------|-----------------|----------------|--------------------------|-------------------------------|
| 0 | 0 | 202.60657 | 252.42885 | **None** | MS:1002312 | len=838, `[65,100,55,108,64,0,0,0,...]` | len=1560, `[64,184,23,0,...]` |
| 1 | 0 | 252.96545 | 296.15249 | **None** | MS:1002312 | len=787 | len=1520 |
| 2 | 0 | 307.15215 | 349.60400 | **None** | MS:1002312 | len=399 | len=670 |

- First 8 bytes of each numpress block = the **fixed point as a big-endian IEEE-754 double** (row0 = `10599266.0`). This is the standard MSNumpress prefix layout.
- `mz_chunk_start`/`mz_chunk_end` are the true first/last m/z of the chunk (kept as plain f64 anchors even though the values themselves are inside the numpress bytes). They still carry transform `MS:1003901` and `sorting_rank:0` — i.e. numpress mode does NOT change the anchor columns' metadata, only adds the bytes column and nulls `mz_chunk_values`.

### EXACTLY how numpress differs from the Phase-2 delta schema

| aspect | Phase-2 delta (`MzPeakSpectrumWriter.ChunkStructField`) | Numpress (reference) |
|--------|--------------------------------------------------------|----------------------|
| `chunk_encoding` value | `MS:1003089` | **`MS:1002312`** |
| `mz_chunk_values` | populated (delta list) | present-but-**NULL** (every row) |
| `mz_numpress_linear_bytes` | **field absent** | present, `large_list<uint8 not null>`, populated |
| intensity carrier | plain `intensity` list `large_list<float>` (transform MS:1003902, buffer_format chunk_secondary) | **`intensity_numpress_slof_bytes`** `large_list<uint8 not null>` (transform MS:1002314, buffer_format chunk_transform); plain `intensity` list **absent** *(Option A)* |
| struct field count | 6 | 7 (m/z-only / Option B) or 8 (reference / Option A) |

**Phase-2 SUMMARY note that needs correcting:** the SUMMARY says "Numpress-linear = its own CURIE" and "in delta mode the column is ABSENT (verified: `ChunkingStrategy::Delta::extra_arrays() -> vec![]`)" — both confirmed. But it also assumed only ONE extra array (mz). The reference adds **two** extra arrays (mz linear + intensity slof) because the reference was built with intensity SLOF on.

---

## Item 2 — Transform / array_index recording

### Decisions

- The numpress transform is recorded in **two places only**: (1) the Arrow column metadata on each chunk leaf, and (2) the `spectrum_array_index` footer JSON. It is **NOT** recorded as a CURIE in `data_processing_method_list` — the reference records only a free-text "conversion options" string there. `[VERIFIED]`
- Literal Numpress-linear transform CURIE = **`MS:1002312`** (confirmed against the file: it is both the `chunk_encoding` column value AND the `transform` on the `mz_numpress_linear_bytes` entry). `[VERIFIED]`
- Literal Numpress-SLOF (intensity) transform CURIE = **`MS:1002314`**. `[VERIFIED]`
- The m/z anchor columns (`chunk_start`/`chunk_end`/`chunk_values`/`chunk_encoding`) keep transform **`MS:1003901`** — same as the delta file. Only the new bytes columns use `MS:1002312`/`MS:1002314`. `[VERIFIED]`
- `buffer_format` for both numpress-bytes entries = **`chunk_transform`** (a new buffer_format vs Phase-2's `chunk_values`/`chunk_secondary`; it is in the validator's allowed enum). `[VERIFIED]`

### Exact `spectrum_array_index` footer JSON our writer must emit (numpress, reference / Option A)

The footer has **6 entries** (4 m/z anchor entries with MS:1003901, then the numpress m/z bytes entry, then the numpress intensity bytes entry). Verbatim from the file:

```json
{
  "prefix": "chunk",
  "entries": [
    {"context":"spectrum","path":"chunk.mz_chunk_start","data_type":"MS:1000523","array_type":"MS:1000514",
     "array_name":"m/z array","unit":"MS:1000040","buffer_format":"chunk_start","transform":"MS:1003901",
     "data_processing_id":null,"buffer_priority":"primary","sorting_rank":0},
    {"context":"spectrum","path":"chunk.mz_chunk_end","data_type":"MS:1000523","array_type":"MS:1000514",
     "array_name":"m/z array","unit":"MS:1000040","buffer_format":"chunk_end","transform":"MS:1003901",
     "data_processing_id":null,"buffer_priority":"primary","sorting_rank":0},
    {"context":"spectrum","path":"chunk.mz_chunk_values","data_type":"MS:1000523","array_type":"MS:1000514",
     "array_name":"m/z array","unit":"MS:1000040","buffer_format":"chunk_values","transform":"MS:1003901",
     "data_processing_id":null,"buffer_priority":"primary","sorting_rank":0},
    {"context":"spectrum","path":"chunk.chunk_encoding","data_type":"MS:1000523","array_type":"MS:1000514",
     "array_name":"m/z array","unit":"MS:1000040","buffer_format":"chunk_encoding","transform":"MS:1003901",
     "data_processing_id":null,"buffer_priority":"primary","sorting_rank":0},
    {"context":"spectrum","path":"chunk.mz_numpress_linear_bytes","data_type":"MS:1000523","array_type":"MS:1000514",
     "array_name":"m/z array","unit":"MS:1000040","buffer_format":"chunk_transform","transform":"MS:1002312",
     "data_processing_id":null,"buffer_priority":"primary","sorting_rank":0},
    {"context":"spectrum","path":"chunk.intensity_numpress_slof_bytes","data_type":"MS:1000521","array_type":"MS:1000515",
     "array_name":"intensity array","unit":"MS:1000131","buffer_format":"chunk_transform","transform":"MS:1002314",
     "data_processing_id":null,"buffer_priority":"primary","sorting_rank":null}
  ]
}
```

Diff vs the Phase-2 `ChunkedSpectrumArrayIndex` const (in `MzPeakSpectrumWriter.cs` lines 64-81):
- entries 1-4 (start/end/values/encoding) are **identical** to Phase-2.
- the Phase-2 5th entry `chunk.intensity` (buffer_format `chunk_secondary`, transform `MS:1003902`, sorting_rank null) is **replaced** by TWO entries: `chunk.mz_numpress_linear_bytes` (MS:1002312) and `chunk.intensity_numpress_slof_bytes` (MS:1002314).
- Under **Option B (m/z-only numpress)** instead: keep the Phase-2 5th `chunk.intensity` entry verbatim and insert only the `chunk.mz_numpress_linear_bytes` entry (5 entries total). The plain intensity list stays.

### `data_processing` block — what the reference actually records

The reference `mzpeak_index.json` `metadata.data_processing_method_list` records NO numpress CURIE. It carries a free-text option string:

```json
{
  "id": "mzpeak_conversion1",
  "methods": [{
    "order": 1,
    "parameters": [{
      "accession": null, "name": "conversion options", "unit": null,
      "value": "--intensity-numpress-slof -c numpress:50 --chromatogram-chunked-encoding delta:50 -y -z -u small.mzML -o small.numpress.mzpeak"
    }],
    "software_reference": "mzpeak_prototyping_convert1"
  }]
}
```

**What our writer must emit in `data_processing`:** Per CONTEXT.md the writer must add a `data_processing` method step recording the numpress transform and flag that default m/z is lossy. The reference proves only a free-text note is *required for validation* (the validator does not check for a numpress CURIE in data_processing). Recommended: add a method step with a parameter carrying the numpress CURIE(s) by accession (MS:1002312 [+ MS:1002314 if Option A]) AND/OR a human-readable lossy-m/z note. This is stricter than the reference but harmless and satisfies the CONTEXT requirement. Keep our existing `version` + `cv_list` (the reference omits both — see Item 4).

---

## Item 3 — Fixed point / precision

### Decisions

- The reference uses **per-chunk optimal (auto) linear fixed point** — NOT a single fixed scale. Verified: for the first 50 chunks, the file's stored fixed point (first 8 bytes, BE double) **equals `pynumpress.optimal_linear_fixed_point(decoded)` exactly** (50/50 exact match). Across all 488 chunks there are **365 distinct fixed-point values** (min 1,080,328 · max 10,733,754 · median 2,040,620). `[VERIFIED]`
- Resulting m/z error bound (the L2 bound our writer must meet) = the numpress quantization half-step **`0.5 / fixed_point`**:
  - worst-case (smallest fp = 1,080,328): **4.628e-07 Th** abs.
  - typical (median fp): **2.45e-07 Th** abs.
  - relative: ~2.3e-09 at m/z 200, ~2.3e-10 at m/z 2000. `[VERIFIED]`
- Numpress-linear at the optimal fixed point is **idempotent**: decode → re-encode at optimal → decode gives **0.0 drift** across all 488 chunks. Round-tripping our own numpress output will not accumulate error. `[VERIFIED]`

### Concrete decode sample (spectrum 0, first 5 m/z)

```
numpress decoded : 202.606575, 202.606824, 202.607072, 202.607321, 202.607569
chunked (delta)  : 202.606575, 202.606823, 202.607072, 202.607321, 202.607569
```
The per-value difference is ~1e-6 Th (within the 0.5/fp bound), consistent with lossy numpress vs lossless delta.

> **L2 caveat for the verifier (not a schema fact):** the numpress and chunked reference files are **different conversions with different scan/point selections** (numpress: 488 chunks, ~217k decoded m/z values; chunked: fewer points per spectrum, only 14 spectra overlap and even those differ in count). A positional whole-file diff between the two reference files is therefore meaningless. The L2 verification for OUR writer must compare **our numpress output decoded vs our own `--lossless`/`--point` output of the SAME small.RAW** (which Phase-2 proved bit-identical to source), asserting `max |Δm/z| <= 0.5/fp_used` per chunk (≈5e-7 Th worst case). Intensity under Option A is SLOF-lossy (do NOT assert bit-exact); under Option B intensity stays bit-exact f32.

### pynumpress decode artifact (hand-off note to the C# port sub-report)

`pynumpress.decode_linear` (and canonical MSNumpress `decodeLinear`) can emit a **phantom extrapolated leading value** for some chunks: in 154/488 chunks `decoded[0] == mz_chunk_start`, but in the rest `decoded[1] == mz_chunk_start` and `decoded[0]` is a ~0.09-Th extrapolation seed. The true chunk data is anchored by `mz_chunk_start`/`mz_chunk_end`; a correct decoder aligns to those anchors (drop the leading phantom when `|decoded[0]-mz_chunk_start| > |decoded[1]-mz_chunk_start|`). The C# port's decode + the L2 round-trip test MUST handle this — it is the single biggest decode correctness trap. (Detailed handling is the C#-port sub-report's responsibility.)

---

## Item 4 — Validator L2 / numpress rules

### Decisions

- mzpeak-validate (profile mzpeak-0.9) has **NO numpress-specific rule, NO L2 rule, NO bytes-decodability rule, and does NOT require the transform CURIE be declared in cv_list.** `[VERIFIED]`
- The `spectra_data` schema rule treats chunk/numpress layouts as **point columns optional** ("their decoding is a v1 TODO") so numpress is never false-failed for missing point columns. `[VERIFIED — schema/tables/spectra_data.columns.json]`
- The array_index `buffer_format` enum **already includes `chunk_transform`**, so the numpress entries pass the array_index schema check. `[VERIFIED — schema/json/array_index.json]`
- `cv_list_consistency` gathers CV codes **only from inflected column NAMES in `spectra_metadata`/`chromatograms_metadata`** (pattern `${CV}_${ACCESSION}_${name}`). The numpress transform CURIEs live in spectra_data **column metadata + footer**, which the cv rule does NOT scan. So MS:1002312/MS:1002314 do **not** trigger a cv_list requirement. The rule only requires the CV *codes* (MS, UO) used by metadata columns be declared. `[VERIFIED — rules/cv.rules.json]`
- The grouped-monotonic m/z check (`p_grouped_monotonic`) reads `point.mz` / `chunk.*`; on numpress it no-ops because the decodable m/z column is absent (numpress bytes are not read). Numpress thus passes monotonicity trivially. `[VERIFIED — core.py]`

### Reference validation result (run this session)

`mzpeak-validate refs/mzPeak/small.numpress.mzpeak` → **FAIL (2 errors, 1 warning)** — but the **identical 2 errors appear on `small.chunked.mzpeak`** too:

```
WARNING profile_resolution   no mzpeak version declared; defaulted to latest profile (mzpeak-0.9)
ERROR   cv_list_declared     metadata.cv_list is absent/empty but the archive uses CV codes ['MS','UO']
ERROR   index_schema_valid   mzpeak_index.json: schema violation at metadata: 'version' is a required property
```

**Interpretation:** both errors are the reference Rust writer's own omissions (no `cv_list`, no index `version`), NOT numpress-specific. **There are ZERO numpress-specific findings.** Our TRFP writer already emits both `version` and a generated `cv_list` (Phase 1/2 validated 0/0), so it does not inherit these. *(Caveat: the system `mzpeak-validate` is the anaconda build on python 3.7; it ran successfully and is the same binary CONTEXT references.)*

### What our numpress output must do to keep 0 errors

1. Keep emitting `metadata.version` and a generated `cv_list` (already done — do not regress).
2. Keep `mz_chunk_start`/`mz_chunk_end` ascending per spectrum with `sorting_rank:0` (they remain plain f64 anchors → monotonicity check still passes; numpress bytes are not decoded by the validator).
3. Use `buffer_format: "chunk_transform"` and a valid CURIE `transform` (MS:1002312 [/MS:1002314]) on the bytes entries (in the allowed enum).
4. cv_list need NOT contain MS:1002312/MS:1002314 to pass the validator — but registering them (the writer already calls `CollectPrefix(ChunkEncodingCurie)` for the delta CURIE) is harmless and more correct. Since `chunk_encoding` value becomes `MS:1002312`, register **MS:1002312** (and MS:1002314 under Option A) the same way Phase-2 registers MS:1003089. **Verify** the validator still passes 0/0 after adding them (version policy only warns on NEWER-than-pinned versions, not on extra codes).

---

## Item 5 — Size sanity

### Decisions

- Numpress is materially smaller, matching the ~63% headline. `[VERIFIED]`

| archive | `spectra_data.parquet` (bytes) | total archive (bytes) |
|---------|-------------------------------:|----------------------:|
| `small.numpress.mzpeak` | 483,960 | 844,899 |
| `small.chunked.mzpeak` (delta) | 1,565,834 | 2,317,492 |
| `small.mzpeak` (point) | 1,683,335 | 2,039,569 |

- **Data-facet delta:** numpress `spectra_data` is **69.1% smaller** than delta-chunked (483,960 vs 1,565,834).
- **Total-archive delta:** **63.5% smaller** (844,899 vs 2,317,492) — matches the CONTEXT "~63% smaller overall."
- **Attribution caveat:** the 63% overall is achieved with **BOTH** m/z linear AND intensity SLOF numpress (Option A). Under **Option B (m/z-only)** the saving will be smaller — SLOF compresses the intensity bytes substantially, so dropping intensity numpress forfeits a large slice of the reduction. The verification target "numpress default < delta-chunked < point" still holds either way, but "~50-65% of chunked" (CONTEXT) is only met under Option A.

---

## Assumptions Log

| # | Claim | Section | Risk if Wrong |
|---|-------|---------|---------------|
| A1 | The reference numpress mode Numpress-encodes intensity (SLOF, MS:1002314) and DROPS the plain intensity list; CONTEXT says intensity stays lossless f32. The two are mutually exclusive. **[ASSUMED scope decision — needs user confirmation]** | Critical Finding / Item 1 | If the user wants true byte-parity with `small.numpress.mzpeak`, intensity SLOF MUST be in scope (Option A) and the "intensity lossless" CONTEXT line is wrong. If intensity must stay lossless (Option B), our output is a valid but NON-reference-identical archive and the "schema diff vs small.numpress.mzpeak" and "~63%" verifications cannot pass as written. |
| A2 | Adding MS:1002312/MS:1002314 to cv_list keeps the validator at 0/0 (not strictly required; assumed harmless given version policy only warns on newer-than-pinned). | Item 4 | If a pinned-CV resolution rule rejects an unknown accession at error severity, registration could introduce a finding — must be re-verified on OUR output. (Low risk: cv rule scans metadata column names, not these CURIEs.) |

---

## Hand-offs

- **To the C# MSNumpress port sub-report:** (1) the phantom-leading-value decode artifact (Item 3) is the top correctness risk for the L2 round-trip test; align decode to `mz_chunk_start`/`mz_chunk_end`. (2) Use **per-chunk `optimalLinearFixedPoint`** (the reference does; matches exactly). (3) Fixed point is stored as the first 8 bytes BE double — standard MSNumpress prefix.
- **To the planner:** resolve A1 (intensity SLOF in/out) before task breakdown — it changes the struct field count (7 vs 8), the footer entry count (5 vs 6), the cv_list registration set, and which verifications are achievable. The Phase-2 `ChunkedSpectrumArrayIndex` const and `ChunkStructField()` are the exact edit points (`MzPeakSpectrumWriter.cs` lines 64-81 and 788-795); branch on `chunk_encoding == MS:1002312`.

## Sources

### Primary (HIGH confidence — direct artifact dumps this session)
- `refs/mzPeak/small.numpress.mzpeak` — spectra_data.parquet schema, per-row field population, footer `spectrum_array_index`, file-level KV (`spectrum_count=48`, `spectrum_data_point_count=25832`), `mzpeak_index.json` metadata + data_processing_method_list, numpress byte decode via pynumpress, per-chunk fixed points.
- `refs/mzPeak/small.chunked.mzpeak` — delta baseline schema + footer for the diff, size baseline.
- `mzpeak-validate` (profile mzpeak-0.9) run on both files this session.
- `~/Claude/mzPeakValidator/mzpeak_validator/profiles/mzpeak-0.9/` — spectra_data.columns.json, array_index.json (chunk_transform enum), cv.rules.json, numeric.rules.json, auxiliary_array.json; `core.py` (declared_sorted / p_grouped_monotonic).
- `ThermoRawFileParser/Writer/MzPeakSpectrumWriter.cs` (ChunkStructField, ChunkedSpectrumArrayIndex const, cv prefix registration), `Writer/MzPeak/MzPeakChunkCodec.cs` (null-aware delta decode used to reconstruct the chunked baseline).

### Tooling
- `python3.11` (3.11.3), `pyarrow 24.0.0`, `pynumpress` (decode_linear / decode_slof / encode_linear / optimal_linear_fixed_point).

## Metadata

**Confidence breakdown:**
- Numpress chunk schema + CURIEs + footer: HIGH — verbatim pyarrow dumps.
- Fixed point = per-chunk optimal + L2 bound 0.5/fp: HIGH — file-fp == pynumpress.optimal exactly (50/50), idempotent round-trip.
- Validator has no numpress/L2 rule + identical errors on both refs: HIGH — direct rule-file read + live validator run.
- Intensity-SLOF scope (Option A vs B): MEDIUM as a *decision* (reference fact is HIGH; the CONTEXT conflict is a genuine open scope question).

**Research date:** 2026-06-14
**Valid until:** stable (reference artifacts + pinned validator profile are fixed); re-verify if the mzpeak-0.9 profile or the reference files change.
