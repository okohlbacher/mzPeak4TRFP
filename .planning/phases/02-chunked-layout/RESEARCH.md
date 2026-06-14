# Phase 2: Chunked Layout - Research

**Researched:** 2026-06-14
**Domain:** mzPeak chunked spectra_data layout (delta-encoded m/z) in a streaming Parquet.Net writer
**Confidence:** HIGH (schema, encoding, decode algorithm, list-column streaming all verified against the reference + a live dotnet spike + a 14/14 round-trip)

## Summary

The HUPO reference's own chunked output (`refs/mzPeak/small.chunked.mzpeak`) was dumped with python3.11 + pyarrow 24.0.0 and cross-read against the reference Rust source (`src/chunk_series.rs`, `src/filter.rs`, `src/writer.rs`). Every CONTEXT guess was confirmed *except three* that the research corrects and the planner must absorb:

1. **Only `spectra_data` is chunked. `spectra_peaks` stays POINT layout** (centroids), identical to v1. [VERIFIED: pyarrow dump]
2. **There is NO `mz_numpress_linear_bytes` column in delta mode.** The chunk struct has exactly **6 fields**. The Numpress byte column is added *only* when the encoding is NumpressLinear (Phase 3), not present-but-empty in delta mode. [VERIFIED: pyarrow dump + `ChunkingStrategy::extra_arrays` returns `vec![]` for Delta]
3. **`chunk_encoding` is the CURIE `"MS:1003089"`, not the literal string `"delta"`.** [VERIFIED: pyarrow dump]

The delta encoding is **null-aware** (`null_delta_encode`/`null_delta_decode` in `src/filter.rs`): both `mz_chunk_values` and `intensity` are `large_list` whose *items can be null*, and nulls are structural gap markers produced by `nullify_zero_intensity` (interior runs of ≥2 consecutive zero intensities are nulled in both arrays). The decode I implemented in Python reproduces the point-layout multiset **exactly: 14/14 spectra, 177,742 == 177,742 points, 0 mismatches.** A live Parquet.Net 5.0.1 dotnet spike proved `list<double>` (nullable items) + `list<float>` (nullable items) write and read back correctly through the exact `NestedLevels` machinery already in the repo.

**Primary recommendation:** Add a `ChunkFacetStream` (sibling of `PointFacetStream`) that buffers points per scan, runs `null_chunk_every_k` (50 m/z window) + `null_delta_encode` + the `is_zero_pair_mask` nullification, and emits one `chunk` struct row per non-empty window via the existing streaming `Handle`. Keep `spectra_peaks` and `chromatograms_data` on the existing `PointFacetStream`. Intensity element type = **f32** (matches the reference and our canonical; validator is dtype-agnostic for chunk).

<user_constraints>
## User Constraints (from CONTEXT.md)

### Locked Decisions
- Emit the data facets in the reference **chunk layout** instead of point layout; make chunked the **new default** (`--point` restores v1 point layout). Lossless (delta-encoded m/z). Numpress (Phase 3) plugs into the `mz_numpress_linear_bytes` slot later. Chromatograms stay point layout (the reference keeps them point).
- Authoritative reference is `refs/mzPeak/small.chunked.mzpeak`.
- Chunk struct per the reference: `chunk<spectrum_index:u64, mz_chunk_start:f64, mz_chunk_end:f64, mz_chunk_values:list<f64>, chunk_encoding:string, intensity:list<?>, mz_numpress_linear_bytes:list<u8>>`. **Confirm intensity element type** and **whether `mz_numpress_linear_bytes` is present-but-empty in delta mode** — RESEARCH corrects both below.
- Chunking: fixed m/z window over the sorted m/z axis, default 50 m/z (configurable). One chunk row per non-empty window per spectrum.
- Encoding: `chunk_encoding="delta"` (lossless); `mz_chunk_values` = consecutive deltas; first m/z is `mz_chunk_start`; `mz_numpress_linear_bytes` empty/null in delta mode. — RESEARCH corrects the CURIE value and the deltas-vs-null-markers detail below.
- Streaming: reuse the Phase-1 streaming handle; chunk rows stream in row groups exactly like point rows. Needs `list<f64>`/`list<f32>` scalar-list columns with rep/def levels.
- `spectrum_array_index` updated to chunk buffer formats + `sorting_rank:0`; cv_list stays exhaustive.
- `--point` flag restores v1 point layout; chunked is default. Both must pass mzpeak-validate.
- `chromatograms_data` stays point layout.

### Claude's Discretion
- Whether to keep canonical f32 intensity or match the reference exactly (validator to arbitrate) — RESEARCH recommends f32.
- `mz_chunk_start`/`end` semantics (window-boundary vs actual-extent) — RESEARCH confirms actual-extent (first/last non-null m/z in the window).
- Default window value and configurability — RESEARCH recommends 50 m/z, expose as an option.

### Deferred Ideas (OUT OF SCOPE)
- Numpress linear compression of `mz_chunk_values` (Phase 3). The `mz_numpress_linear_bytes` column belongs to Phase 3, NOT this phase.
- Chromatogram chunking (reference keeps chromatograms point; CONTEXT keeps them point).
</user_constraints>

<phase_requirements>
## Phase Requirements

| ID | Description | Research Support |
|----|-------------|------------------|
| CHUNK-01 | Emit `spectra_data` in chunk layout | Authoritative chunk schema (§Standard Stack), exact struct fields |
| CHUNK-02 | Delta-encode m/z losslessly | Exact encode/decode algorithm + 14/14 round-trip proof (§Delta Encoding) |
| CHUNK-03 | Fixed m/z window chunking, default 50, configurable | `null_chunk_every_k` algorithm + default (§Chunking Strategy) |
| CHUNK-04 | Update `spectrum_array_index` footer for chunk | Exact footer JSON delta (§array_index) |
| CHUNK-05 | `--point` flag restores v1 point layout; chunked default | Both paths reuse existing facet streams (§Streaming) |
| CHUNK-06 | Pass mzpeak-validate in both modes; lossless vs point | Validator behavior + decisive round-trip test (§Validator) |
</phase_requirements>

## Architectural Responsibility Map

| Capability | Primary Tier | Secondary Tier | Rationale |
|------------|-------------|----------------|-----------|
| m/z windowing + delta encode | Writer (MzPeakSpectrumWriter facet stream) | — | Per-scan transform of the (mz,intensity) arrays before column build |
| Nullable list-column build (rep/def levels) | MzPeakParquet (NestedLevels/Handle) | — | Already owns the general nested-level computer; list-of-scalar is a strict simplification of the existing list-of-struct |
| Row-group streaming to temp file + STORED zip | Phase-1 Handle + AddStoredFromFile | — | Unchanged; chunk rows are just a different row "unit" |
| Footer KV (`spectrum_array_index`, counts) | MzPeakSpectrumWriter footer constants | — | Mode-specific footer string selected by `--point` vs chunked |
| Layout selection (`--point`) | CLI option → writer ctor flag | — | Single boolean switches facet-stream type for spectra_data |

## Standard Stack

### Core
| Library | Version | Purpose | Why Standard |
|---------|---------|---------|--------------|
| Parquet.Net | 5.0.1 | Nested struct + nullable list columns, ZSTD, streaming row groups | Already the TRFP dependency; Phase-1 streaming Handle built on it [VERIFIED: csproj + live spike] |
| python3.11 + pyarrow | 24.0.0 | Reference schema dump + round-trip verification (test-side) | Reads the reference `large_list` chunk struct; not a runtime dependency [VERIFIED: in session] |
| mzpeak-validate (mzpeak_validator) | profile mzpeak-0.9, catalog 1.6 | Conformance gate | The arbiter per CONTEXT [VERIFIED: ran in session] |

### Alternatives Considered
| Instead of | Could Use | Tradeoff |
|------------|-----------|----------|
| Reuse `NestedLevels`/`Handle` | New low-level column writer | Unnecessary — the spike proves the existing machinery handles nullable list-of-scalar |
| f32 intensity | f64 intensity | Reference uses f32 and validator is dtype-agnostic for chunk; f64 would bloat the file with no conformance benefit |

**Installation:** No new packages. (Test-side only, already present: `python3.11 -m pip install jsonschema` to silence validator JSON-Schema warnings.)

## Package Legitimacy Audit

No external packages are installed by this phase. Parquet.Net 5.0.1 is a pre-existing, verified dependency carried since v1. No new registry packages → no slopcheck gate required.

## Authoritative Target Chunk Schema (spectra_data.parquet, chunked mode)

Exact reference Arrow schema [VERIFIED: pyarrow dump of `small.chunked.mzpeak`]:

```
chunk: struct<
  spectrum_index:  uint64,
  mz_chunk_start:  double,
  mz_chunk_end:    double,
  mz_chunk_values: large_list<item: double>,   # item NULLABLE
  chunk_encoding:  string,                       # CURIE value "MS:1003089"
  intensity:       large_list<item: float>       # item NULLABLE, f32
>
```

**Exactly 6 fields. NO `mz_numpress_linear_bytes` in delta mode.** [VERIFIED: `st.num_fields == 6`; Rust `ChunkingStrategy::Delta::extra_arrays() -> vec![]`]

Per-field metadata in the reference (each field carries Arrow field metadata; the planner should attach the equivalent in the footer `spectrum_array_index`, see §array_index — Parquet.Net does not need to set Arrow field-level metadata since the v1 writer already relies on the footer JSON, not Arrow field metadata, to pass the validator):

| Field | array_accession | data_type_accession | buffer_format | transform | unit | sorting_rank |
|-------|-----------------|---------------------|---------------|-----------|------|--------------|
| mz_chunk_start | MS:1000514 | MS:1000523 | chunk_start | MS:1003901 | MS:1000040 | 0 |
| mz_chunk_end | MS:1000514 | MS:1000523 | chunk_end | MS:1003901 | MS:1000040 | 0 |
| mz_chunk_values | MS:1000514 | MS:1000523 | chunk_values | MS:1003901 | MS:1000040 | 0 |
| chunk_encoding | MS:1000514 | MS:1000523 | chunk_encoding | MS:1003901 | MS:1000040 | 0 |
| intensity | MS:1000515 | MS:1000521 | chunk_secondary | MS:1003902 | MS:1000131 | (null) |

**Parquet.Net schema declaration (planner copies this shape):**
```csharp
new StructField("chunk",
    new DataField<ulong>("spectrum_index"),
    new DataField<double>("mz_chunk_start"),
    new DataField<double>("mz_chunk_end"),
    new ListField("mz_chunk_values", new DataField<double>("item", true)),  // nullable item
    new DataField<string>("chunk_encoding"),
    new ListField("intensity", new DataField<float>("item", true)));         // nullable item
```

**large_list vs list:** Parquet.Net 5.0.1 emits a 32-bit-offset `list`, the reference uses `large_list` (64-bit offsets). This is the **same accepted deviation already shipped in v1** for `large_string`/`large_list` metadata fields. The mzPeak Python reader and `mzpeak-validate` read both transparently (the validator's chunk check only asserts a top-level `chunk` column exists). [VERIFIED: validator structural rule `data_kind_has_facet` checks `tops & {"point","chunk"}`; v1 already passes with plain list/string]

### spectra_peaks stays POINT layout (do NOT chunk it)
[VERIFIED: pyarrow dump — `spectra_peaks.parquet` in the chunked archive is `point: struct<spectrum_index:uint64, mz:double, intensity:float>`, identical to v1.] The existing `PointFacetStream` for peaks is unchanged.

## Delta Encoding (exact algorithm + worked example)

### Decisions
- **`chunk_encoding` value = `"MS:1003089"`** (the DELTA_ENCODE CURIE), stored as a plain `string` column. NOT `"delta"`. [VERIFIED: pyarrow + `src/chunk_series.rs:56`]
- **`mz_chunk_start` / `mz_chunk_end` = first / last NON-NULL actual m/z in the window** (actual extent, not window boundary). [VERIFIED: `encode_arrow` takes `it.next()` / `it.next_back()` over non-null values]
- **`mz_chunk_values` is null-aware delta-encoded** and does **NOT** include the start value as its first element. The start value lives in `mz_chunk_start` only. [VERIFIED: `null_delta_encode` + `ChunkingStrategy::Delta` arm]
- **Lossless requirement met** by reproducing the v1 point multiset exactly — proven 14/14 below. [VERIFIED in session]

### Encode algorithm (mirror of `null_delta_encode`, `src/filter.rs:788`)
Input: the window's m/z slice `mz[0..k]` and intensity slice `int[0..k]`, AFTER `nullify_zero_intensity` has set both `mz[i]` and `int[i]` to **null** wherever `is_zero_pair_mask` is true (a zero intensity whose previous OR next intensity is also zero — i.e. interior of a zero-run; flanking single zeros are kept).

`mz_chunk_start = first non-null mz`, `mz_chunk_end  = last non-null mz`.

`mz_chunk_values` (length == k, one element per point INCLUDING nulls, but the first element is dropped per the delta rule below):

```
buffer = []
it = iter(mz)            # mz is the (nullified) window slice
last = it.next()         # first element
if last is None: buffer.append(None)     # leading null kept as a marker
for item in it:          # remaining elements
    if item is not None:
        if last is not None:
            buffer.append(item - last)   # delta
            last = item
        else:
            buffer.append(item)          # ABSOLUTE (previous was null → store raw m/z)
            last = item
    else:
        buffer.append(None)              # null marker
        last = None
return buffer            # length == len(mz) - 1  (+1 if a leading null was appended)
```
Key rule: **after a null, the next real value is stored ABSOLUTE (raw m/z), not as a delta.** That is why row 1+ in the dump shows `[None, 252.965..., 0.0003874..., ...]` — the `252.965` is an absolute restart, then deltas resume.

`intensity` list = the (nullified) intensity slice verbatim (nullable f32 items). Its length == k.

### Decode algorithm (mirror of `null_delta_decode`, `src/filter.rs:826`) — VERIFIED CORRECT
```python
def null_delta_decode(arr, start):   # arr = mz_chunk_values, start = mz_chunk_start
    buf = []; last = start
    if arr[0] is None:
        if len(arr) > 1 and arr[1] is None:
            buf.append(last)        # singleton peak exactly at the chunk boundary
        last = None
    else:
        buf.append(start)           # start is the FIRST reconstructed m/z
    for item in arr:
        if item is not None:
            if last is not None:
                d = item + last; buf.append(d); last = d   # cumulative delta
            else:
                buf.append(item); last = item               # absolute restart
        else:
            buf.append(None); last = None
    return buf                       # decoded m/z (length == len(intensity)), nulls aligned
```
Reconstructed `(mz, intensity)` = `zip(decoded_mz, intensity)`, **dropping pairs where either is null** (those are the nullified interior-zero points the reader treats as absent).

### Worked example (row 1, spectrum 0, from the actual file)
```
mz_chunk_start = 252.96545361882283
mz_chunk_values[:6] = [None, 252.96545361882283, 0.00038742706865946275, 0.00038742825540794, ...]
intensity[:6]       = [None, 1328.2458, 3272.7817, 4725.3662, ...]
```
Decode:
- `arr[0] is None` and `arr[1]` is NOT None → do not push start; set `last=None`.
- iterate: `None` → push None (last=None). `252.965...` (last is None) → push **252.96545361882283** absolute (last=252.965). `0.000387427...` (last set) → push `252.96545361882283 + 0.00038742706865946275 = 252.96584104589149` (delta). next delta → `252.96584104589149 + 0.00038742825540794 = ...` etc.
- Result mz `[None, 252.96545361882283, 252.96584104589149, ...]`, zipped with intensity, drop the leading `(None,None)` → first real point `(252.96545361882283, 1328.2458)`.

### Round-trip proof (decisive lossless test) — RAN IN SESSION
Decoded the entire chunked `spectra_data` and compared the per-spectrum `(round(mz,6), intensity)` multiset to the point-layout `spectra_data` (both from the reference, same source):
```
chunked non-null total: 177742
point   non-null total: 177742
EXACT-match spectra: 14/14   mismatch: 0
```
[VERIFIED in session] This is the encode/decode contract the writer must reproduce.

## Chunking Strategy

### Decisions
- **Window = fixed 50.0 m/z**, the reference default. [VERIFIED: `src/writer.rs:1217,1431` `ChunkingStrategy::Delta { chunk_size: 50.0 }`]
- **Expose as a configurable option** (e.g. `--chunk-size`/CLI param) defaulting to 50.0, per CONTEXT discretion.
- **One chunk row per NON-empty window**, ordered, per spectrum, in ascending m/z. Spectra appear in ascending `spectrum_index`; their chunks are contiguous and ordered. [VERIFIED: dump — spectrum 0 has 36 chunks, then spectrum 1, etc.; chunk rows grouped + ordered]
- **`mz_chunk_start`/`end` = actual extent** (first/last non-null m/z in the window), confirmed above.

### Windowing algorithm (mirror of `null_chunk_every_k`, `src/filter.rs:871`)
Partition the per-spectrum (already sorted, already null-marked) m/z array into intervals each spanning ≤ `width` m/z:
- `threshold = first_non_null_mz + width`.
- Walk indices; when a non-null `mz[i] > threshold`, close the current chunk at `i` and advance `threshold` by `width` until `threshold >= mz[i]`.
- Null pairs are skipped together (the loop steps past paired nulls so a chunk boundary never lands mid-null-pair).
- **A length-1 chunk is avoided** (`if i - offset != 1`): the splitter won't emit a singleton chunk; it rolls the point into the adjacent chunk.
- The final residual interval `[offset, n)` is always emitted.

Note: because the threshold advances by `width` from the *first* m/z (not from fixed 50-m/z grid lines), chunk windows are anchored to the spectrum's m/z minimum, not to global 0/50/100 boundaries. Chunk widths are therefore "≤ 50 m/z from the chunk's own start," which is why `mz_chunk_end - mz_chunk_start` in the dump is ~40–50, variable. [VERIFIED: row 0 start=202.6 end=252.4 ≈ 49.8 span]

### Pipeline order (per scan, before column build) — IMPORTANT
1. Get sorted `(mz[], intensity[])` for the scan (already produced by the existing per-scan path).
2. **Nullify zero-pairs:** compute `is_zero_pair_mask` over intensity; set the masked positions to null in **both** mz and intensity. (`is_zero_pair_mask`, `src/filter.rs:704`: position `i` is masked iff `intensity[i]==0` AND (previous was zero OR next is zero).)
3. Partition with `null_chunk_every_k(mz, 50.0)`.
4. For each interval: `mz_chunk_start/end` = first/last non-null mz; `mz_chunk_values` = `null_delta_encode(mz_slice)`; `intensity` list = `intensity_slice` (nullable); `chunk_encoding = "MS:1003089"`; `spectrum_index = ordinal`.
5. Emit one chunk struct row per interval through the streaming Handle.

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Rep/def levels for nullable list-of-scalar | Bespoke level arrays | Existing `MzPeakParquet.NestedLevels` + `ListOf(levels, hasValue)` | Already correct for list leaves (`data: list<byte>` aux array uses it); spike confirmed for double/float |
| Streaming chunk rows to STORED zip | New writer | Phase-1 `Handle.WriteRowGroupAsync` + `AddStoredFromFile` | Chunk rows are just a different row unit; the seekable-temp-file path is unchanged |
| Delta encode/decode | New numeric scheme | Mirror `null_delta_encode`/`null_delta_decode` byte-for-byte | The reference reader expects EXACTLY this null-aware scheme; any deviation breaks round-trip |
| Window partitioning | Naive `floor(mz/50)` bucketing | Mirror `null_chunk_every_k` | Naive bucketing produces different chunk boundaries (global grid vs first-mz-anchored) and would not match the reference; also must skip null-pairs and avoid singleton chunks |

**Key insight:** The reference's "delta" layout is not a plain consecutive-delta list. It is a *null-marked, zero-pair-suppressed, absolute-restart-after-null* scheme. Reproducing it faithfully is the entire difficulty of this phase; the Parquet plumbing is already solved.

## Runtime State Inventory

This is a code/format change, not a rename or data migration. No stored datastores, live-service config, OS-registered state, secrets/env vars, or build artifacts embed the renamed string.
- **Stored data:** None — output archives are regenerated per run; no persistent store keyed on layout.
- **Live service config:** None — no external service involved.
- **OS-registered state:** None.
- **Secrets/env vars:** None new (DOTNET_ROLL_FORWARD already used; unchanged).
- **Build artifacts:** None beyond normal `bin/Release` rebuild.

## Common Pitfalls

### Pitfall 1: Assuming `mz_numpress_linear_bytes` must exist in delta mode
**What goes wrong:** Adding an empty `list<uint8>` column to match the CONTEXT's struct sketch produces a 7-field struct that does NOT match the reference's 6-field struct.
**Why it happens:** The CONTEXT struct sketch lists the Numpress column; the Rust source adds it only for NumpressLinear.
**How to avoid:** Emit exactly 6 fields in delta mode. The Numpress column is a Phase-3 concern.
**Warning signs:** pyarrow diff of our output vs reference shows an extra column.

### Pitfall 2: Storing the start value as the first `mz_chunk_values` element
**What goes wrong:** Off-by-one m/z reconstruction; first point doubled or shifted.
**Why it happens:** A naive "first value + cumsum" reading. The reference stores deltas only; the start lives in `mz_chunk_start`.
**How to avoid:** `mz_chunk_values` length == `len(mz_slice) - 1` (no leading null), or `== len(mz_slice)` only when a leading null is present. Decode starts from `mz_chunk_start`.
**Warning signs:** Round-trip point count is `chunk_count + N` instead of equal.

### Pitfall 3: Treating nulls as deltas (forgetting the absolute-restart rule)
**What goes wrong:** After a null gap, applying the next value as a delta yields a wrong m/z.
**Why it happens:** The encode stores the post-null value as an **absolute** m/z, not a delta.
**How to avoid:** In encode, when `last is None` push the raw value; in decode, when `last is None` accept the value as absolute.
**Warning signs:** Decoded m/z jumps to ~0 or ~original after every gap.

### Pitfall 4: Naive `floor(mz/50)` windowing
**What goes wrong:** Different chunk boundaries than the reference; singleton chunks; boundaries inside null-pairs.
**Why it happens:** The reference anchors windows to the first m/z and advances the threshold; it also skips null-pairs and avoids length-1 chunks.
**How to avoid:** Mirror `null_chunk_every_k`. (Round-trip still passes regardless of boundary choice as long as decode is inverse of encode — but matching the reference avoids a pyarrow schema/shape diff and future surprises.)

### Pitfall 5: large_list expectation
**What goes wrong:** A test asserting `large_list` fails because Parquet.Net emits `list`.
**How to avoid:** Assert on item *type* (double/float) and on round-trip values, not on the list-offset width. The validator and Python reader accept both.

## Code Examples

### Verified Parquet.Net list-of-scalar column build (from the live spike)
```csharp
// schema leaf path is "chunk/mz_chunk_values/list/item" (note the "/list/" segment)
var fVal = (DataField)schema.GetDataFields().First(f => f.Path.ToString() == "chunk/mz_chunk_values/list/item");
var fInt = (DataField)schema.GetDataFields().First(f => f.Path.ToString() == "chunk/intensity/list/item");
// spike measured: list item MaxDefinitionLevel = 4, MaxRepetitionLevel = 1

// Per chunk row, build LeafRow entries with the existing helpers:
//   present item   -> def = MaxDefinitionLevel        (4)
//   null  item     -> def = MaxDefinitionLevel - 1    (3)
//   rep            -> 0 for the first item of the row's list, MaxRepetitionLevel (1) thereafter
int vP = fVal.MaxDefinitionLevel, vN = fVal.MaxDefinitionLevel - 1, vRep = fVal.MaxRepetitionLevel;
// row whose mz_chunk_values == [delta, null, delta]:
var row = MzPeakParquet.ListOf(new[]{vP, vN, vP}, new[]{true, false, true});
// NestedLevels(fVal, rows) -> (defLevels, repLevels); only NON-null values go in the value Array.
var (def, rep) = MzPeakParquet.NestedLevels(fVal, rows);
await rg.WriteColumnAsync(new DataColumn(fVal, nonNullDoubleValues, def, rep));
```
Spike round-trip output (values correct, nulls preserved):
```
chunk/mz_chunk_values/list/item = [0.1, null, 0.3, null, 250]
chunk/intensity/list/item       = [10, 20, 30, null, 99]
```
[VERIFIED: ran on Parquet.Net 5.0.1, net via DOTNET_ROLL_FORWARD, arch -arm64]

**Streaming carries it unchanged:** the existing `Handle.WriteRowGroupAsync(schema, cols)` writes each `schema.GetDataFields()` leaf from the `cols` dictionary. List leaves are ordinary `DataField`s in that walk; no Handle change is required. The new facet stream mirrors `PointFacetStream` (buffer per scan → flush at `Cap` → `Close(finalMetadata)`), differing only in that one chunk *row* carries variable-length lists (so the cap counts chunk rows, not points). **Gotcha vs point columns:** point columns are flat (def/rep = null); chunk list columns MUST pass computed `def`/`rep` arrays. Also the value Array passed to `DataColumn` contains only the non-null elements (nulls are expressed purely via def-levels), exactly as the aux-array `data` list already does.

## array_index Footer Delta

### Decisions
- Chunked `spectrum_array_index` has **5 entries** (one per non-index chunk field), prefix `"chunk"`. [VERIFIED: footer dump]
- m/z entries carry `transform: "MS:1003901"`; intensity carries `transform: "MS:1003902"`. The v1 point footer in our writer currently OMITS `transform` (and still validates) — for chunked, **include the transform CURIEs** to match the reference and to register the delta/null-marking transform. [VERIFIED: footer dump; `transform` is NOT in the array_index.json `required` list, so it is additive-safe]
- `sorting_rank: 0` on all four m/z entries; **`sorting_rank: null` (omit) on the intensity entry** — matches both the reference chunk footer and the v1 point footer. [VERIFIED: footer dump]
- Register the new CURIEs in `cv_list`: `MS:1003089` (delta encoding), `MS:1003901` (m/z transform), `MS:1003902` (intensity transform), plus the existing `MS:1000514/MS:1000523/MS:1000040/MS:1000515/MS:1000521/MS:1000131`. The v1 cv_list machinery (`CollectPrefix`) must see these so cv_list stays exhaustive. [CITED: CONTEXT "cv_list stays exhaustive (any new transform/encoding CURIE registered)"]

### Exact chunked footer JSON (target)
```json
{"prefix":"chunk","entries":[
 {"context":"spectrum","path":"chunk.mz_chunk_start","data_type":"MS:1000523","array_type":"MS:1000514","array_name":"m/z array","unit":"MS:1000040","buffer_format":"chunk_start","transform":"MS:1003901","data_processing_id":null,"buffer_priority":"primary","sorting_rank":0},
 {"context":"spectrum","path":"chunk.mz_chunk_end","data_type":"MS:1000523","array_type":"MS:1000514","array_name":"m/z array","unit":"MS:1000040","buffer_format":"chunk_end","transform":"MS:1003901","data_processing_id":null,"buffer_priority":"primary","sorting_rank":0},
 {"context":"spectrum","path":"chunk.mz_chunk_values","data_type":"MS:1000523","array_type":"MS:1000514","array_name":"m/z array","unit":"MS:1000040","buffer_format":"chunk_values","transform":"MS:1003901","data_processing_id":null,"buffer_priority":"primary","sorting_rank":0},
 {"context":"spectrum","path":"chunk.chunk_encoding","data_type":"MS:1000523","array_type":"MS:1000514","array_name":"m/z array","unit":"MS:1000040","buffer_format":"chunk_encoding","transform":"MS:1003901","data_processing_id":null,"buffer_priority":"primary","sorting_rank":0},
 {"context":"spectrum","path":"chunk.intensity","data_type":"MS:1000521","array_type":"MS:1000515","array_name":"intensity array","unit":"MS:1000131","buffer_format":"chunk_secondary","transform":"MS:1003902","data_processing_id":null,"buffer_priority":"primary"}
]}
```
The footer also keeps `spectrum_count` and `spectrum_data_point_count` (the reference uses `spectrum_data_point_count`; our v1 footer key naming should be preserved as-is for the point path and reused for chunked — the planner should reuse the existing `PointFooter` count keys, only swapping `spectrum_array_index`). [VERIFIED: chunked footer keys = `spectrum_count`, `spectrum_data_point_count`, `spectrum_array_index`, `ARROW:schema`]

**`spectra_peaks` footer stays the v1 point `spectrum_array_index`** (peaks remain point layout).
**`--point` mode** uses the existing v1 `SpectrumArrayIndex` constant unchanged.

## Intensity Element Type Decision

**Recommendation: f32 (keep canonical).** [VERIFIED]
- The reference `small.chunked.mzpeak` stores chunk `intensity` as `large_list<item: float>` = **f32**. [pyarrow dump]
- Our v1 canonical is f32. So f32 matches BOTH the reference and our existing point output → lossless multiset comparison is exact (the round-trip used f32 and matched 14/14).
- The validator does **not** constrain chunk-facet dtype: the `mz_dtype_data`/`intensity_dtype_data` rules are gated by `guard: point.intensity` and no-op on chunk layout; the only chunk check is `data_kind_has_facet` (top-level `chunk` column present). [VERIFIED: validator core.py + numeric.rules.json]
- f64 would double m/z-equivalent intensity storage for zero conformance gain.

## State of the Art

| Old Approach (CONTEXT guess) | Current Approach (verified) | Impact |
|------------------------------|-----------------------------|--------|
| `chunk_encoding = "delta"` | `chunk_encoding = "MS:1003089"` (CURIE) | Writer must emit the CURIE string |
| `mz_numpress_linear_bytes` present-but-empty in delta | Column ABSENT in delta (6-field struct) | Do not add the column this phase |
| `mz_chunk_values` = plain consecutive deltas, first m/z = start | Null-aware deltas with absolute-restart-after-null + zero-pair nullification | Encode/decode must mirror `null_delta_*` |
| Both data facets chunked | Only `spectra_data` chunked; `spectra_peaks` stays point | Keep peaks on PointFacetStream |

**Deprecated/outdated:** The archived v1 "differential decode" note ("first point at `mz_chunk_start`, then `mz += delta` over N-1 deltas") is the *happy-path* subset; it is correct for a gapless chunk but misses null markers + absolute restarts. Use the full `null_delta_decode`.

## Assumptions Log

| # | Claim | Section | Risk if Wrong |
|---|-------|---------|---------------|
| A1 | Including `transform` CURIEs in the chunk footer (which v1 point omits) will not regress the validator | array_index | LOW — `transform` is additive and not in the schema `required` list; validator JSON-Schema check is the only consumer and it permits extra fields. Mitigate: run validator after first chunked build. |
| A2 | `mzpeak-validate` will pass our chunked output at 0 errors even though the reference `small.chunked.mzpeak` itself reports 1 error (`cv_list_declared`) | Validator | LOW — our v1 writer already emits an exhaustive cv_list (the reference's 1 error is its own cv_list omission, which we do NOT replicate). Mitigate: assert 0 errors on OUR output, not parity with the reference's error count. |
| A3 | Matching the reference's exact window boundaries (`null_chunk_every_k`) is not required for losslessness, only for schema/shape parity | Chunking | LOW — round-trip is preserved by any inverse encode/decode pair; mirroring the reference is recommended for faithfulness and future Numpress alignment. |

## Open Questions

1. **Should `mz_chunk_start`/`end` ever equal 0.0 for an all-null/empty window?**
   - What we know: the Rust `encode_arrow` returns `(0.0, 0.0, empty)` for an empty/all-null slice, and `decode_arrow` early-returns 0 points when `start==0 && end==0`.
   - What's unclear: TRFP spectra are non-empty after the existing per-scan filtering, so empty windows shouldn't arise; `null_chunk_every_k` already omits empty windows.
   - Recommendation: Do not emit a chunk row for an empty window (mirror the reference: only non-empty intervals produce rows). No special 0.0 handling needed in practice; add a guard test for an all-zero-intensity spectrum.

2. **CLI surface for `--chunk-size`.**
   - What we know: default 50.0 is locked; CONTEXT says configurable.
   - What's unclear: exact flag name / whether it ships this phase.
   - Recommendation: expose an internal/CLI option defaulting to 50.0; planner decides flag naming consistent with existing TRFP CLI conventions.

## Environment Availability

| Dependency | Required By | Available | Version | Fallback |
|------------|------------|-----------|---------|----------|
| dotnet (native arm64) | build/test | ✓ | 9.0.17 / 10.0.9 (8.0 via DOTNET_ROLL_FORWARD) | — |
| Parquet.Net | writer | ✓ | 5.0.1 | — |
| python3.11 + pyarrow | test-side round-trip + schema diff | ✓ | 24.0.0 | — |
| mzpeak_validator | conformance gate | ✓ | profile mzpeak-0.9 | — |
| jsonschema (for validator) | full validator run (else warnings) | ✓ (installed in session for 3.11) | latest | validator still runs; missing → JSON-Schema checks downgrade to warnings |

**Missing dependencies with no fallback:** None.
**Missing dependencies with fallback:** jsonschema — if absent the validator emits WARNING (not ERROR) on JSON-Schema checks; install `python3.11 -m pip install jsonschema` for a clean run.

## Validator + Size Sanity

### Validator behavior (RAN IN SESSION)
- The reference `small.chunked.mzpeak` itself reports **1 error (`cv_list_declared` — it omits cv_list)** + environmental WARNINGs (offline HUPO schema URLs). [VERIFIED]
- The chunk facet is otherwise accepted: the only structural chunk requirement is a top-level `chunk` column (`data_kind_has_facet`); dtype rules are gated to point layout and no-op on chunk. [VERIFIED]
- **Our bar:** OUR chunked output must report **0 errors**. We already ship an exhaustive cv_list, so we strictly beat the reference. `--point` mode must also stay at 0 errors (unchanged v1 path).

### Size sanity (RAN IN SESSION)
- spectra_data inner Parquet: **chunked 1,565,834 B vs point 1,683,335 B ≈ 7% smaller.** [VERIFIED]
- **CAVEAT — do NOT compare whole archives across the two reference files:** they were generated with different peak-picking settings (chunked archive `spectra_peaks` = 50,247 rows vs point = 25,344 rows), so the chunked *archive* is larger purely due to a larger peaks facet — NOT due to chunking. For OUR writer (same RAW, same peaks, only `spectra_data` layout changes), the chunked archive WILL be smaller because only `spectra_data` differs and it shrinks ~7%. Plan the size assertion as **chunked `spectra_data` byte size < point `spectra_data` byte size for the same RAW**, not whole-archive.

### Cheapest decisive round-trip test
Mirror the session's Python check as a test (or a C# equivalent):
1. Convert `small.RAW` twice: once chunked (default), once `--point`.
2. Decode the chunked `spectra_data` with `null_delta_decode` per chunk; drop null pairs.
3. Assert the per-`spectrum_index` `(round(mz,6), intensity)` multiset **equals** the `--point` `spectra_data` multiset. (Session proved this is exact: 14/14, 177,742==177,742.)
4. Assert both archives validate 0 errors; assert chunked `spectra_data` byte size < point.
This single test covers losslessness (CHUNK-02), layout (CHUNK-01), `--point` (CHUNK-05), and size — the highest-signal check per unit effort. Add narrow unit tests for: chunk_encoding == "MS:1003089"; footer `spectrum_array_index` shape; window span ≤ 50; nullable-list column round-trip (the spike, hardened into a test).

## Sources

### Primary (HIGH confidence)
- `refs/mzPeak/small.chunked.mzpeak` — pyarrow 24.0.0 dump: chunk struct (6 fields), field metadata, footer `spectrum_array_index`, encoding CURIE, sizes, row counts.
- `refs/mzPeak/small.mzpeak` — point-layout reference for the round-trip multiset comparison.
- `refs/mzPeak/src/chunk_series.rs` — `ChunkingStrategy`, `encode_arrow`, `decode_arrow`, `from_arrays`, `to_struct_array`, `to_schema`, `extra_arrays`.
- `refs/mzPeak/src/filter.rs` — `null_delta_encode` (788), `null_delta_decode` (826), `null_chunk_every_k` (871), `is_zero_pair_mask` (704).
- `refs/mzPeak/src/writer.rs` — default `chunk_size: 50.0`.
- `refs/mzPeak/schema/array_index.json` — entry `required` fields (transform optional).
- `~/Claude/mzPeakValidator/mzpeak_validator/core.py` + `profiles/mzpeak-0.9/rules/*.json` + `schema/tables/spectra_data.columns.json` — chunk facet handling, dtype gating.
- Live Parquet.Net 5.0.1 dotnet spike — nullable list<double>/list<float> write+read round-trip, leaf path `.../list/item`, item MaxDef=4/MaxRep=1.
- Live Python decode round-trip — 14/14 spectra, 177,742==177,742, 0 mismatch.

### Secondary (MEDIUM confidence)
- `ThermoRawFileParser/Writer/MzPeak/MzPeakParquet.cs`, `Writer/MzPeakSpectrumWriter.cs` — existing streaming Handle + PointFacetStream + footer constants + NestedLevels.
- `.planning/phases/01-streaming-writer-per-scan-robustness/01-01-SUMMARY.md` — streaming handle API + STORED-zip assembly.

## Metadata

**Confidence breakdown:**
- Authoritative chunk schema: HIGH — direct pyarrow dump of the reference.
- Delta encode/decode: HIGH — mirrored from Rust source AND proven by 14/14 round-trip.
- Chunking strategy/defaults: HIGH — Rust source + dump-confirmed spans.
- Intensity type: HIGH — dump + validator source confirm f32 is correct and unconstrained.
- array_index footer: HIGH — exact footer dump.
- List-column streaming: HIGH — live Parquet.Net 5.0.1 spike.
- Validator/size: HIGH — validator ran in session; size measured; cross-file archive caveat surfaced.

**Research date:** 2026-06-14
**Valid until:** 2026-07-14 (stable; reference artifacts and Parquet.Net version are pinned)
