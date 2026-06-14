# Phase 4: Chromatograms + Conformance Verification - Research

**Researched:** 2026-06-14
**Domain:** Thermo chromatogram (TIC) extraction, mzPeak chromatogram facets, conformance verification (validator gate + differential vs mzML2mzPeak + L1/L2 round-trip)
**Confidence:** HIGH (every claim below verified by tool: codebase grep with file:line, pyarrow dumps of the real reference archive, and live end-to-end runs of the reference pipeline)

## Summary

All six unknowns are resolved by direct inspection, not inference. The TIC source is the Thermo
device chromatogram API (`TraceType.TIC` via `GetChromatogramData`), reused exactly as
`MzMlSpectrumWriter.ConstructChromatograms()` already calls it for the base-peak trace
[file:line cited below]. The ground-truth `chromatograms_data.parquet` carries **one TIC point per
scan, 1:1 aligned with the spectra** — `time` == per-scan RT (minutes, f64), `ms_level` == the
originating scan's ms_level (1 or 2, **NOT 0 and NOT a fixed 1**), `intensity` == the device TIC
trace value at that scan (f32). `chromatograms_metadata.parquet` is one row: `id="TIC"`,
`scan_polarity=0`, `chromatogram_type="MS:1000235"`, with fully-present-but-null `precursor` and
`selected_ion` structs.

The validator introduces **no new structural rule** for chromatograms (there is no
`chromatograms_metadata.columns.json`, and the only data-facet rule `data_kind_has_facet` gates
`entity_type` `spectrum`/`mass spectrum`, not `chromatogram`). The sole chromatogram-aware checks are
`cv_inflection_chromatograms_metadata` and `cv_list_declared`; both already pass because the new
column accessions are MS-prefixed (already declared MS/UO). 0/0 is preserved.

VER-02's reference path **works end-to-end** with the prebuilt `mzml2mzpeak` binary, but reveals two
EXPECTED, fundamental divergences that the comparison must accommodate as **semantic** equivalence,
not byte/schema identity: (1) `mzml2mzpeak` always writes a **chunk** layout (delta+zlib,
`MS:1003089`) for spectra_data, never our **point** layout; (2) it routes **profile MS1 → spectra_data,
centroid MS2 → spectra_peaks**, whereas TRFP puts every spectrum's profile signal in spectra_data
(point) and additionally writes centroids to spectra_peaks. After decoding chunks and zero-stripping,
the **nonzero (m/z,intensity) multisets match exactly** (verified: spec0 11057==11057, spec1
14815==14815). The reference path also does **not** carry the TIC chromatogram (mzdata reads 0
chromatogram points from the TRFP mzML), so VER-02 cannot diff the TIC against the reference — the TIC
is validated by VER-01 (validator) + VER-04 (self round-trip), not VER-02.

**Primary recommendation:** Build the TIC by capturing `(RT, ms_level)` per scan in the existing
Phase 3 loop and pairing with the device `TraceType.TIC` trace (one value per scan, in scan order);
emit it as the chromatogram `point`/metadata facets per the exact ground-truth shapes below. For
VER-02, decode the reference chunk layout in pyarrow and compare nonzero (m/z,intensity) multisets
per spectrum-index keyed by representation routing. For L1/L2, trust TRFP's own `spectra_data` as the
post-read truth (no second RAW read needed). Shell out to the prebuilt arm64 binaries
(`mzpeak-validate`, `mzml2mzpeak`) with skip-with-warning when absent.

## Architectural Responsibility Map

| Capability | Primary Tier | Secondary Tier | Rationale |
|------------|-------------|----------------|-----------|
| TIC extraction | Thermo RawFileReader (device API) | TRFP writer loop | Instrument exposes the device TIC trace; recomputing by summing spectra would diverge from the reference |
| Chromatogram facet emission | TRFP writer (`MzPeakSpectrumWriter`) | `MzPeakParquet` (point + nested writer) | Reuse the established point-facet + cv_list mechanism; extend, don't fork |
| Index/cv_list deltas | TRFP writer (`BuildIndex`/`AddMetadataBlocks`) | — | cv_list is generated from collected CV prefixes; chromatogram columns route through `Cv()` |
| Validator gate (VER-01/03) | external `mzpeak-validate` (python3.11/arm64) | NUnit shell-out | Authoritative conformance oracle; test invokes it |
| Differential (VER-02) | external `mzml2mzpeak` (arm64) + pyarrow | NUnit/pytest harness | Reference converter is the independent second implementation |
| L1/L2 round-trip (VER-04) | pyarrow read-back of our `.mzpeak` | TRFP `spectra_data` as truth | Cheapest decisive truth is our own post-read signal |

---

## Decision 1 — TIC extraction from Thermo

**DECISION:** Use the Thermo device TIC trace via `GetChromatogramData(TraceType.TIC)` — the exact
pattern `MzMlSpectrumWriter.ConstructChromatograms()` already uses for the base-peak trace — and
align it 1:1 with the per-scan `(RT, ms_level)` already collected in the Phase 3 spectrum loop.
**Do NOT recompute the TIC by summing `ScanStatistics.TIC`**: the reference's TIC intensity is the
device trace value, which differs from `spectrum.total_ion_current` for MS1 scans (verified:
ref TIC point0 = 15245068 vs spectrum0 stored TIC = 71263168; they match only for MS2).

### Where the existing writer does this

`ThermoRawFileParser/Writer/MzMlSpectrumWriter.cs:917-968` — `ConstructChromatograms()`:

```csharp
// MzMlSpectrumWriter.cs:930-938  (base-peak; the TIC is the identical call with TraceType.TIC)
_rawFile.SelectInstrument(Device.MS, 1);
settings = new ChromatogramTraceSettings(TraceType.BasePeak);
data  = _rawFile.GetChromatogramData(new IChromatogramSettings[] { settings }, -1, -1);
trace = ChromatogramSignal.FromChromatogramData(data);   // trace[i].Times, trace[i].Intensities
```

`TraceType.TIC` is a real enum member (used at `XIC/XicReader.cs:119` with `TraceType.MassRange`;
the TIC CV term `MS:1000235 "total ion current chromatogram"` is already in
`OntologyMapping.cs:1051-1059` under the `"tic"` key). `-1, -1` means "whole run". The returned
`ChromatogramSignal` exposes `Times` (List<double>, **minutes** — Thermo native, matching
`UO:0000031` and the ground-truth time scale 0.0049..0.487) and `Intensities` (List<double>).

### Verified 1:1 alignment (the key fact that shapes the snippet)

The ground-truth TIC has **exactly 48 points for 48 scans**, and elementwise:
`TIC.time[i] == spectrum.time[i]` (true), `TIC.ms_level[i] == spectrum.ms_level[i]` (true),
monotonic non-decreasing time (true). So the device TIC trace yields one value per scan in scan
order — pair it directly with the per-scan record already built in the loop.

### Recommended snippet (drop into `MzPeakSpectrumWriter.Write`, after the scan loop)

```csharp
// Device TIC over the whole run (minutes, one value per scan, scan order).
raw.SelectInstrument(Device.MS, 1);
var ticSettings = new ChromatogramTraceSettings(TraceType.TIC);
var ticData  = raw.GetChromatogramData(new IChromatogramSettings[] { ticSettings }, -1, -1);
var ticTrace = ChromatogramSignal.FromChromatogramData(ticData);

// trace[0] is the MS TIC; pair its (time,intensity) with each record's ms_level by ordinal.
var chromTime      = new List<double>();   // f64, minutes  -> point.time
var chromIntensity = new List<float>();    // f32           -> point.intensity
var chromMsLevel   = new List<long>();     // i64           -> point.ms_level  (per-scan ms_level)
if (ticTrace.Length > 0 && ticTrace[0].Times.Count == records.Count)
{
    for (int i = 0; i < records.Count; i++)
    {
        chromTime.Add(ticTrace[0].Times[i]);
        chromIntensity.Add((float)ticTrace[0].Intensities[i]);
        chromMsLevel.Add(records[i].MsLevel);
    }
}
// Fallback if the device trace length != scan count (filtered MS levels / range subset):
// re-key by matching ticTrace[0].Scans (scan numbers) to records by scan number, OR fall back to
// records[i].Time + (float)records[i].TotalIonCurrent. Note the fallback intensity will NOT
// match the reference device TIC for MS1 — acceptable only when the device trace is unavailable.
```

`records[i].MsLevel` and `records[i].Time` already exist (`MzPeakSpectrumWriter.cs:54-58,186-187`).
The TIC carries the **scan's** ms_level (1 or 2), confirmed against ground truth.

**Confidence:** HIGH — exact API call grepped at MzMlSpectrumWriter.cs:935; 1:1 alignment proven by
pyarrow elementwise comparison against the real reference archive.

---

## Decision 2 — chromatograms ground-truth shapes

Verified by pyarrow dump of `refs/mzPeak/small.unpacked.mzpeak/chromatograms_{data,metadata}.parquet`.

### `chromatograms_data.parquet`

Schema (field order is load-bearing — match exactly):

```
point: struct<chromatogram_index: uint64, time: double, intensity: float, ms_level: int64>
```

- **1 chromatogram** (chromatogram_index distribution: `{0: 48}`), **48 points** = 48 scans.
- `time`: f64, **minutes**, == per-scan RT.
- `intensity`: f32, device TIC value per scan.
- `ms_level`: **i64, the per-scan ms_level** (distribution `{1: 14, 2: 34}`). **NOT 0, NOT fixed 1.**
- Per-child field metadata (drives the `chromatogram_array_index` KV, below):
  - `time`: data_type `MS:1000523`, array_type `MS:1000595` (time array), unit `UO:0000031`, sorting_rank 0
  - `intensity`: data_type `MS:1000521`, array_type `MS:1000515`, unit `MS:1000131`
  - `ms_level`: data_type `MS:1000522`, array_type `MS:1000786` (non-standard data array), unit `UO:0000186`
- Footer KV: `chromatogram_array_index` (JSON, prefix "chunk"... actually `"prefix":"point"`),
  `chromatogram_data_point_count`, plus the same file-level metadata blocks
  (`run`/`software_list`/`file_description`/`instrument_configuration_list`/`data_processing_method_list`/`sample_list`/`scan_settings_list`).
  Observed `chromatogram_data_point_count` = `0` in the reference (a known reference quirk; our
  spectra facets already write `"0"` here too — keep parity).

The exact `chromatogram_array_index` JSON to emit (verbatim from ground truth):

```json
{"prefix":"point","entries":[
 {"context":"chromatogram","path":"point.time","data_type":"MS:1000523","array_type":"MS:1000595","array_name":"time array","unit":"UO:0000031","buffer_format":"point","buffer_priority":"primary","sorting_rank":0},
 {"context":"chromatogram","path":"point.intensity","data_type":"MS:1000521","array_type":"MS:1000515","array_name":"intensity array","unit":"MS:1000131","buffer_format":"point","buffer_priority":"primary"},
 {"context":"chromatogram","path":"point.ms_level","data_type":"MS:1000522","array_type":"MS:1000786","array_name":"ms level","unit":"UO:0000186","buffer_format":"point","buffer_priority":"primary"}
]}
```
(The reference serializes `transform:null`/`data_processing_id:null`/`sorting_rank:null` on the 2nd/3rd
entries; our writer omits null keys in `SpectrumArrayIndex` and still passes the validator, so omit them.)

### `chromatograms_metadata.parquet`

Three top-level struct columns (`chromatogram`, `precursor`, `selected_ion`) — same packed-parallel
shape as spectra_metadata. **1 row.** The `precursor`/`selected_ion` structs are **present-but-null**
(every leaf null), exactly the shape CONTEXT.md requires.

`chromatogram` struct, verified row 0 values:

| Field | Type | TIC value |
|-------|------|-----------|
| `index` | uint64 | `0` |
| `id` | large_string | `"TIC"` |
| `MS_1000465_scan_polarity` | int8 | **`0`** (not ±1 — neutral/unset for the TIC) |
| `MS_1000626_chromatogram_type` | string | **`"MS:1000235"`** (total ion current chromatogram CURIE as a cell value) |
| `data_processing_ref` | large_string | `null` |
| `MS_1003060_number_of_data_points` | uint64 | `48` |
| `parameters` | large_list<PARAM> | `[]` (empty) |
| `auxiliary_arrays` | large_list<AUX_ARRAY> | `[]` (empty) |
| `number_of_auxiliary_arrays` | uint32 | `0` |

`precursor` and `selected_ion` are the same structs as the spectra metadata facet (full nested shape,
all leaves null on this single row). Reuse `BuildPrecursorField()`/`BuildSelectedIonField()`
(`MzPeakSpectrumWriter.cs:538-563`) — but note the ground-truth chromatogram `selected_ion` also
carries `ion_mobility_value`/`ion_mobility_type` (the spectra one in our writer does **not** — see
the dump). For a single all-null row this is shape-only; emit the struct fully null. The spectra-side
`selected_ion` omits ion_mobility today and still passes; the chromatogram `selected_ion` may
likewise omit them since all values are null and there is no validator rule on chromatogram columns.

Footer KV on metadata: `chromatogram_count` (`1`), `chromatogram_data_point_count` (`0`).

**chromatogram_type CURIE:** TIC = **`MS:1000235`** (confirmed in the metadata cell). Base-peak would
be `MS:1000628` (in `OntologyMapping.cs:1042` and the mzML base-peak path at
`MzMlSpectrumWriter.cs:947`), but Phase 4 writes only the TIC. `MS:1000626` is the generic
"chromatogram type" attribute term used as the **column name suffix** (`MS_1000626_chromatogram_type`),
not the value.

**Confidence:** HIGH — direct pyarrow dump of the real HUPO-PSI reference archive.

---

## Decision 3 — index + cv_list deltas

**DECISION:** Add the two chromatogram entries to `files[]` and route the new chromatogram column
accessions through the existing `Cv()` collector. No cv_list version bump or new schema needed.

### `mzpeak_index.json` `files[]` — add (verified ground-truth entries):

```json
{"name":"chromatograms_metadata.parquet","entity_type":"chromatogram","data_kind":"metadata"},
{"name":"chromatograms_data.parquet","entity_type":"chromatogram","data_kind":"data arrays"}
```

(Ground-truth ordering puts metadata before data; order is not validator-significant.) Extend
`BuildIndex` (`MzPeakSpectrumWriter.cs:1043-1083`) to append these when chromatograms are written.

### CV accessions that must flow into the generated cv_list

The generated cv_list is collected from every accession routed through `Cv()`/`CvParam()`/`JParam()`
into `_cvPrefixes` (`MzPeakSpectrumWriter.cs:565-577`, 871-893). New accessions introduced by
chromatograms:

| Accession | Where it appears | Prefix already covered? |
|-----------|------------------|--------------------------|
| `MS:1000235` | chromatogram_type **cell value** | MS — yes (cell value, NOT inflected by validator anyway) |
| `MS:1000626` | `chromatogram_type` **column name** | MS — yes |
| `MS:1000465` | `scan_polarity` column name (chromatogram) | MS — yes (already used by spectra) |
| `MS:1003060` | `number_of_data_points` column name | MS — yes (already used) |
| `MS:1000595` `MS:1000786` `MS:1000522` `MS:1000521` `MS:1000515` | data-facet **field metadata** only | not inflected by validator (see below) |
| `UO:0000031` `UO:0000186` `MS:1000131` | data-facet field metadata / column units | UO/MS — yes |

**cv_list outcome:** Only `{MS, UO}` are referenced — exactly the set Phase 3 already declares. **No
new CV prefix, no version bump.** Continue to route `MS:1000626`/`MS:1000235`/`MS:1000465` etc.
through `Cv()`/`CvParam()` so the assert-coverage logic stays exhaustive (defensive — even though
{MS,UO} are already present).

### Validator chromatogram-specific checks (from `~/Claude/mzPeakValidator`)

Authoritative source: `mzpeak_validator/profiles/mzpeak-0.9/rules/{structural,cv}.rules.json`,
`core.py`.

- **There is NO `chromatograms_metadata.columns.json`** — no `columns_present` rule for chromatograms.
  (`schema/tables/` contains only `spectra_{data,metadata,peaks}.columns.json`.)
- `data_kind_has_facet` gates `entity_types: ["spectrum","mass spectrum"]` only
  (`structural.rules.json:21`) — the chromatogram `data arrays` entry is **not** checked for a `point`
  facet. (It still has one, harmlessly.)
- `cv_inflection_chromatograms_metadata` (`cv.rules.json:18-20`) inflects only **column names** in
  `chromatograms_metadata` matching `${CV}_${digits}_...`. The chromatogram columns
  (`MS_1000465_...`, `MS_1000626_...`, `MS_1003060_...`) all resolve in the pinned MS OBO → no error.
  The `MS:1000235` value lives in a **cell**, not a column name → not inflected.
- `cv_list_declared` (`cv.rules.json:22-24`, `core.py:573-589`) scans `chromatograms_metadata` column
  names for CV codes and requires each in `metadata.cv_list`. Codes are MS/UO → already declared.
- The data-facet field-metadata accessions (`MS:1000595` etc.) are **not** scanned by any rule
  (cv_inflection reads logical column/leaf names, not Arrow child field metadata) — verified by
  reading `core.py::p_cv_inflection` and `_used_cv_codes`.

**Net: adding chromatograms introduces zero new failing rule. 0/0 is preserved** (assuming the
metadata facet exists whenever chromatograms are present — the only chromatogram precondition, which
we satisfy by always writing both facets together).

**Confidence:** HIGH — read every relevant rule file + the validator core, and ran the validator on
both the reference and the current TRFP output.

---

## Decision 4 — Differential harness vs mzML2mzPeak (VER-02)

**DECISION:** Use the **prebuilt arm64 binary** at `~/Claude/mzML2mzPeak/target/release/mzml2mzpeak`
(it already exists — `file` confirms `Mach-O 64-bit executable arm64`). **Do NOT rebuild in-plan**
(cargo release build of a Rust 1.96 / mzdata tree is heavy/slow; the binary runs correctly — verified
`--help` and a real conversion). Provide a one-line fallback build only if the binary is missing.

### Build (only if binary absent)

```bash
cd ~/Claude/mzML2mzPeak && cargo build --release    # rust-toolchain.toml pins 1.96; binary -> target/release/mzml2mzpeak
```

### Exact reference pipeline (verified end-to-end, all three steps run clean)

```bash
DLL=ThermoRawFileParser/bin/x64/Release/net8.0/ThermoRawFileParser.dll
RAW=ThermoRawFileParser/ThermoRawFileParserTest/Data/small.RAW
M2M=~/Claude/mzML2mzPeak/target/release/mzml2mzpeak

# 1) RAW -> PROFILE mzML  (-f 1 = mzML; -p = noPeakPicking => profile, aligns with our spectra_data)
arch -x86_64 ~/.dotnet-x64/dotnet "$DLL" -i "$RAW" -b /tmp/p4/small.profile.mzML -f 1 -p

# 2) mzML -> reference mzpeak  (--no-numpress => lossless m/z, chunk-delta not numpress)
"$M2M" /tmp/p4/small.profile.mzML /tmp/p4/small.ref.mzpeak --no-numpress

# 3) RAW -> our TRFP mzpeak   (-f 4 = MzPeak;  NOTE: 4 not 5 — 5 is None)
arch -x86_64 ~/.dotnet-x64/dotnet "$DLL" -i "$RAW" -b /tmp/p4/small.trfp.mzpeak -f 4
```

Verified outputs: step 1 → 3.4 MB profile mzML with `<chromatogramList count="1">`; step 2 → 48
spectra, 6-member archive; step 3 → "Wrote mzPeak archive with 48 spectra (305213 data points, 12890
peak points)", validator PASS 0/0.

**TRFP CLI facts (verified MainClass.cs:530-533):** `-f` (`--format`): `0`=MGF `1`=mzML
`2`=indexedmzML `3`=Parquet **`4`=mzPeak** `5`=None. `-p`/`--noPeakPicking` (MainClass.cs:548-550) =
profile (PROFILE data to align with our point `spectra_data`). `-b`/`--output`.

**mzML2mzPeak CLI facts (verified README.md:131-150 + `--help`):** direction inferred from extension
(`.mzML` → forward). `--no-numpress` = lossless delta-chunked m/z (REQUIRED for a fidelity diff;
default numpress is lossy on m/z). Output is the positional 2nd arg. There is **no point-layout flag** —
it ALWAYS writes a `chunk` layout.

### CRITICAL: the reference path does NOT carry the TIC chromatogram

`mzml2mzpeak` logged "converted 48 spectra + **0 chromatograms**" and its
`chromatograms_data.parquet` has **0 rows** (degenerate placeholder metadata row: id="",
chromatogram_type="MS:1000626"). mzdata does not surface the TRFP mzML TIC chromatogram as a
data-bearing chromatogram. **Therefore VER-02 cannot compare the TIC** — drop "TIC" from the VER-02
compare list. The TIC is certified by VER-01 (validator) + VER-04 (self round-trip) instead.

**Confidence:** HIGH — ran the full pipeline; binary is prebuilt and arm64.

---

## Decision 5 — Comparison method

### VER-02: differential vs mzML2mzPeak (semantic equivalence, NOT byte/schema identity)

Two EXPECTED divergences make column/schema equality impossible; the test asserts logical content.

**Divergence A — layout.** Reference uses **chunk** layout
(`chunk: struct<spectrum_index, mz_chunk_start:f64, mz_chunk_end:f64, mz_chunk_values:list<f64>,
chunk_encoding:string, intensity:list<f64>>`, encoding `MS:1003089` = delta+zlib). Ours is **point**
layout. Decode the reference to absolute (m/z, intensity) pairs:

```python
# verified reconstruction: absolute mz = chunk_start + cumulative sum of mz_chunk_values;
# intensity[0] pairs with chunk_start, intensity[1:] with the deltas; multiple chunk rows per spectrum.
ref = collections.defaultdict(list)
for r in chunk_rows:
    c = r["chunk"]; si = c["spectrum_index"]; mz = c["mz_chunk_start"]
    ref[si].append((mz, c["intensity"][0]))
    for d, it in zip(c["mz_chunk_values"], c["intensity"][1:]):
        mz += d; ref[si].append((mz, it))
```

(Verified: reconstructed last m/z == `mz_chunk_end` exactly.)

**Divergence B — facet routing.** Reference routes **profile MS1 → spectra_data (chunk)**,
**centroid MS2 → spectra_peaks** (verified: spectra_data indices are exactly the 14 ms_level-1
repr=MS:1000128 spectra; spectra_peaks has the 34 ms_level-2 repr=MS:1000127). TRFP puts ALL 48
spectra's profile in spectra_data (point) and writes centroids to spectra_peaks where a centroid
stream exists.

**Divergence C — profile zero-stripping.** Reference strips flanking zero-intensity profile points;
TRFP keeps them. Verified: spec0 ref_all=13589 vs trfp_all=19913, but **nonzero counts are equal
(11057 == 11057)**; spec1 14815 == 14815.

**VER-02 comparison spec (per spectrum-index `i`):**
1. Build TRFP's per-spectrum signal from `point` (m/z, intensity).
2. Build the reference's per-spectrum signal: decode `chunk` (spectra_data) for profile spectra;
   use `spectra_peaks` for spectra the reference centroided.
3. Compare the **nonzero-intensity (m/z, intensity) multiset** for the spectra the reference carries
   in spectra_data (the MS1 profile set). Match condition: equal m/z (f64, exact — both are lossless),
   equal intensity within f32 tolerance (both narrowed to f32 then read back; use abs tol ~ 1e-3 *
   max, or compare after rounding to f32). Verified exact-equal on nonzero points for the run.
4. Cross-checks (where both expose the value): `spectrum count` (48 == 48), `ms_level` per index,
   `polarity` per index, `RT`/`time` per index, `precursor m/z` + `charge` per MSn index. Do NOT
   compare per-spectrum **total** point counts (zero-stripping differs) — compare **nonzero** counts.
5. **Drop TIC from VER-02** (reference carries none — Decision 4).

EXPECTED divergences to document in the test as not-a-failure: chunk-vs-point layout; profile/centroid
facet routing; flanking-zero retention; nativeID/id string format (TRFP `controllerType=0
controllerNumber=1 scan=N` vs mzML2mzPeak's mzdata-derived id) — assert semantic fields, not the id
string.

### VER-04: L1/L2 round-trip

**Source of truth — RECOMMENDATION:** **trust TRFP's own `spectra_data` as the post-read truth.** Do
NOT write a separate C# RAW dump. Rationale: the conformance claim is "the m/z we read from the RAW
survives to the archive exactly (L1) and intensity survives bounded under the recorded f32 narrowing
(L2)". The values TRFP wrote into `spectra_data` ARE the post-read m/z/intensity; reading them back
via pyarrow and checking the invariants is decisive and cheapest. A second RAW read would only
re-exercise the same `ReadMZData`/`OrderedPairs` code path — no independent signal.

**L1 (m/z, exact f64):** read-back `point.mz` is f64 and must equal what was written byte-for-byte
(parquet is lossless for f64). The meaningful L1 invariant: **m/z within each spectrum is sorted
ascending and the multiset is preserved** (no drop/merge) — mirrors `OrderedPairs`
(MzPeakSpectrumWriter.cs:262-292) and the `sorting_rank:0` claim. Assert: per-spectrum m/z strictly
non-decreasing; count == `spectrum.number_of_data_points`.

**L2 (intensity, bounded under f32 narrowing):** source intensities are f64 from Thermo, written as
f32 (`(float)intensities[i]`, MzPeakSpectrumWriter.cs:271). The recorded transform is the
`data_processing_method_list` entry "intensity narrowing f64→f32" (MzPeakSpectrumWriter.cs:973-976).
L2 invariant: read-back intensity == `(float)` of the value (i.e. exact at f32 width; the narrowing is
the only transform). Assert read-back intensity is finite and equals its own f32 round-trip (no
further loss). This mirrors mzML2mzPeak's L1/L2 framing (README.md:142): L1 = value-equal at canonical
width; L2 = bounded (intensity rel-err ≤ 1e-3).

**Confidence:** HIGH — chunk decode verified against the live reference; nonzero multisets proven
equal; facet routing proven by ms_level/representation.

---

## Decision 6 — NUnit env (VER-03)

**DECISION:** The NUnit test shells out to `mzpeak-validate` (and pyarrow via `python3.11`) and
**skips-with-warning** (NUnit `Assert.Ignore` / `Assert.Warn`) when the tool or interpreter is
absent, so CI without the python toolchain stays green.

### Verified availability (this machine)

| Tool | Path | Version / arch | Notes |
|------|------|----------------|-------|
| `mzpeak-validate` | `/Users/kohlbach/anaconda3/bin/mzpeak-validate` | runs (PASS/FAIL output) | python entrypoint |
| `python3.11` | `/Library/Frameworks/Python.framework/Versions/3.11/bin/python3.11` | 3.11 | use this for pyarrow scripts |
| `python3` (anaconda) | `/Users/kohlbach/anaconda3/bin/python3` | py3.7, pyarrow 12.0.1 | works but py3.7; prefer 3.11 |
| `mzml2mzpeak` | `~/Claude/mzML2mzPeak/target/release/mzml2mzpeak` | arm64, prebuilt | VER-02 only |
| x64 dotnet | `~/.dotnet-x64/dotnet` | runs the x64 Release DLL | conversion runtime |

### Invocation pattern

```csharp
// resolve once; if null -> Assert.Ignore("mzpeak-validate not on PATH; skipping VER-03 gate")
var psi = new ProcessStartInfo("mzpeak-validate", $"--json \"{archivePath}\"")
          { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
// parse JSON: verdict == "PASS", summary.errors == 0  (Phase 3 already does exactly this gate)
```

The Phase 3 test (`MzPeakWriterTests.cs`) already implements the validator gate parsing findings by
error id — **reuse that mechanism**, extend its allowlist check to confirm no chromatogram-related
error id appears, and add structural asserts: archive contains
`chromatograms_metadata.parquet` + `chromatograms_data.parquet`, the index `files[]` has both
`entity_type:"chromatogram"` entries, the metadata facet has 1 row with `id="TIC"` /
`chromatogram_type="MS:1000235"`, and the data facet's point count == spectrum count.

**arm64/x64 note:** the *test host* runs under whatever `dotnet test` uses
(`DOTNET_ROOT_X64=$HOME/.dotnet-x64 dotnet test` per CONTEXT.md). The shelled-out `mzpeak-validate`
(python) and `mzml2mzpeak` (arm64) are independent processes — no arch coupling. Just resolve them by
name/path and skip-with-warning if `Process.Start` throws or the exit indicates absence.

**Confidence:** HIGH — all tools probed live; Phase 3 already runs the validator gate from NUnit.

---

## Standard Stack

No new libraries. Everything reuses existing TRFP dependencies and the Phase 3 mechanism.

| Component | Source | Purpose |
|-----------|--------|---------|
| `ThermoFisher.CommonCore` chromatogram API | already referenced | `TraceType.TIC`, `GetChromatogramData`, `ChromatogramSignal` |
| `Parquet.Net` 5.0.1 | already a TRFP dep | chromatogram facets via `MzPeakParquet` low-level path |
| `MzPeakParquet` (Phase 2/3) | `Writer/MzPeak/MzPeakParquet.cs` | point facet + nested struct/list writer + cv_list collection |
| `mzpeak-validate` (python3.11) | `~/Claude/mzPeakValidator` | VER-01/VER-03 oracle |
| `mzml2mzpeak` (prebuilt arm64) | `~/Claude/mzML2mzPeak/target/release/` | VER-02 reference converter |
| `pyarrow` 12.0.1 (py3.7) / 3.11 | installed | VER-02/VER-04 archive reads |

## Package Legitimacy Audit

No external packages are installed by this phase (all tooling pre-exists; no `npm`/`pip`/`cargo`
install in the plan). Audit: **N/A — no new dependencies.**

## Architecture Patterns

### Reference pipeline (VER-02) data flow

```
small.RAW ──(TRFP, -f1 -p PROFILE)──> small.profile.mzML ──(mzml2mzpeak --no-numpress)──> small.ref.mzpeak [CHUNK layout]
small.RAW ──(TRFP, -f4 MzPeak)──────────────────────────────────────────────────────────> small.trfp.mzpeak [POINT layout]
                                                                                                  │
                                                            pyarrow: decode chunk -> abs(m/z,int) ; compare nonzero multiset per index
```

### TIC build pattern (reuse, don't fork)

The TIC is one extra `point`/metadata facet pair built after the existing scan loop, reusing
`BuildPointFacet`-style logic and `BuildMetadataFacet`-style packed-parallel emission. Add an
`int64 ms_level` column to the chromatogram point struct (the spectra point struct has no ms_level).

### Anti-patterns to avoid

- **Recomputing TIC by summing `ScanStatistics.TIC`.** Diverges from the device trace for MS1
  (verified). Use the device `TraceType.TIC` trace.
- **Comparing VER-02 by schema/column equality.** The reference is chunk-layout, routed differently.
  Compare decoded nonzero (m/z,intensity) multisets.
- **Asserting TIC equivalence in VER-02.** The reference carries no TIC. Validate the TIC via
  VER-01/VER-04 only.
- **Comparing per-spectrum total point counts in VER-02.** Zero-stripping differs; compare nonzero.
- **A second C# RAW dump for VER-04 truth.** Redundant; trust TRFP `spectra_data`.

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| TIC trace | summed per-scan TIC | `GetChromatogramData(TraceType.TIC)` | matches the device/reference value; instrument-exposed |
| Chromatogram CURIE | string literals | `OntologyMapping.GetChromatogramType("tic")` → MS:1000235 | already mapped (OntologyMapping.cs:1051) |
| Nested parquet emission | bespoke writer | `MzPeakParquet` NestedLevels + cv_list collector | Phase 3 mechanism, validator-proven |
| Conformance verdict | custom checks | `mzpeak-validate` JSON | authoritative HUPO-PSI oracle |
| Reference conversion | custom mzML reader | prebuilt `mzml2mzpeak` | independent second implementation |

## Common Pitfalls

### Pitfall 1: TIC point ms_level set to 0 or fixed 1
**What goes wrong:** Writing `ms_level=0` (intuitive for a "summary" trace) or `1`.
**Why:** The ground truth carries the **per-scan** ms_level (1 and 2 interleaved).
**Avoid:** `chromMsLevel.Add(records[i].MsLevel)`. **Warning sign:** ms_level distribution not `{1:N1, 2:N2}`.

### Pitfall 2: `-f 5` instead of `-f 4` for TRFP mzPeak
**What goes wrong:** `5` = None → no output, no error, silent empty result (hit live during research).
**Avoid:** `-f 4` (or `-f mzpeak`). **Warning sign:** no "Wrote mzPeak archive" log line.

### Pitfall 3: numpress default corrupts the VER-02 m/z diff
**What goes wrong:** Without `--no-numpress`, mzml2mzpeak writes lossy fixed-point m/z; the diff fails on m/z.
**Avoid:** always pass `--no-numpress` for the differential. **Warning sign:** small m/z rel-errors ~1e-5.

### Pitfall 4: assuming a chromatogram structural validator rule exists
**What goes wrong:** Over-engineering chromatogram column types to satisfy a non-existent rule.
**Why:** there is no `chromatograms_metadata.columns.json`; no chromatogram `columns_present` rule.
**Avoid:** match the ground-truth shape for fidelity, not validator appeasement. 0/0 is already guaranteed.

### Pitfall 5: comparing the chunk layout as absolute m/z
**What goes wrong:** `mz_chunk_values` are **deltas** (encoding `MS:1003089`), start excluded.
**Avoid:** absolute = `chunk_start + cumsum(values)`; intensity[0] pairs with chunk_start.

## Runtime State Inventory

Not a rename/refactor/migration phase — **N/A** (greenfield feature addition + verification).

## Code Examples

### Decode reference chunk layout (VER-02), verified

```python
import pyarrow.parquet as pq, zipfile, io, collections
z = zipfile.ZipFile("/tmp/p4/small.ref.mzpeak")
rows = pq.read_table(io.BytesIO(z.read("spectra_data.parquet"))).to_pylist()
ref = collections.defaultdict(list)
for r in rows:
    c = r["chunk"]; si = c["spectrum_index"]; mz = c["mz_chunk_start"]
    ref[si].append((mz, c["intensity"][0]))
    for d, it in zip(c["mz_chunk_values"], c["intensity"][1:]):
        mz += d; ref[si].append((mz, it))
# nonzero multiset per spectrum; compare to TRFP point layout
```

### Validator gate (VER-01/VER-03), Phase 3 pattern

```bash
mzpeak-validate --json /tmp/p4/small.trfp.mzpeak   # verdict:"PASS", summary.errors:0
```

## State of the Art

| Old assumption | Reality (verified) | Impact |
|----------------|--------------------|--------|
| TIC = sum of per-scan TIC | TIC = device `TraceType.TIC` trace (differs for MS1) | use the device trace |
| Reference is point-layout, diff column-wise | Reference is chunk-layout, route profile/centroid by facet | decode + semantic compare |
| Reference carries a TIC to diff | Reference carries 0 chromatograms from TRFP mzML | TIC validated by VER-01/04 only |
| `-f 5` = mzPeak | `-f 5` = None; `-f 4` = mzPeak | use 4 |

## Assumptions Log

| # | Claim | Section | Risk if Wrong |
|---|-------|---------|---------------|
| A1 | Device `TraceType.TIC` returns exactly one value per emitted scan in scan order on small.RAW | Decision 1 | If filtered MS-level selection (`-l`) changes scan count, the trace length != records.Count; the snippet's length guard + fallback handles it, but the MS1-faithful TIC intensity is only available from the device trace. Mitigation: re-key by `ticTrace[0].Scans` to scan numbers. |
| A2 | The chromatogram `selected_ion` may omit `ion_mobility_value`/`ion_mobility_type` (all-null row, no validator rule) | Decision 2 | Low — reference shape includes them but values are null; a strict reader expecting the columns might differ. Safe to include them as null for exact shape parity. |

(Both are low-risk; A1 has a coded fallback. Everything else in this research is VERIFIED by tool.)

## Open Questions

1. **Does the device TIC trace length ever differ from scan count under MS-level filtering?**
   - Known: on small.RAW (all levels) it is exactly 48 == scan count.
   - Unclear: under `-l 1` (MS1 only) or a scan-range subset.
   - Recommendation: guard `ticTrace[0].Times.Count == records.Count`; else re-key by
     `ticTrace[0].Scans` (scan numbers) to records, falling back to per-record RT + TIC. The plan
     should include a task asserting the guard path on the default (all-levels) conversion.

## Environment Availability

| Dependency | Required By | Available | Version | Fallback |
|------------|------------|-----------|---------|----------|
| x64 dotnet | conversion | ✓ | `~/.dotnet-x64/dotnet` | none (build-critical) |
| x64 Release DLL | conversion | ✓ | `bin/x64/Release/net8.0/ThermoRawFileParser.dll` (already built) | rebuild via `dotnet build -c Release` |
| `mzpeak-validate` | VER-01/03 | ✓ | anaconda bin (runs) | skip-with-warning in NUnit |
| `python3.11` + pyarrow | VER-02/04 | ✓ | 3.11 (and py3.7 pyarrow 12.0.1) | skip-with-warning |
| `mzml2mzpeak` | VER-02 | ✓ | prebuilt arm64 at target/release | `cargo build --release` (Rust 1.96) |
| `small.RAW` | all | ✓ | `ThermoRawFileParserTest/Data/small.RAW` | none |

**Missing dependencies with no fallback:** none.

## Validation Architecture

### Test Framework
| Property | Value |
|----------|-------|
| Framework | NUnit (existing TRFP test project) |
| Config file | `ThermoRawFileParser/ThermoRawFileParserTest/ThermoRawFileParserTest.csproj` |
| Quick run command | `DOTNET_ROOT_X64=$HOME/.dotnet-x64 dotnet test --filter MzPeak` |
| Full suite command | `DOTNET_ROOT_X64=$HOME/.dotnet-x64 dotnet test` |

### Phase Requirements → Test Map
| Req ID | Behavior | Test Type | Automated Command | File Exists? |
|--------|----------|-----------|-------------------|-------------|
| CHROM-01 | TIC chromatogram_data facet (point: chromatogram_index,time,intensity,ms_level) written, 1 pt/scan, per-scan ms_level | unit | `dotnet test --filter Chromatogram` | ❌ Wave 0 (extend MzPeakWriterTests.cs) |
| CHROM-02 | chromatograms_metadata facet: 1 row, id="TIC", type MS:1000235, polarity 0, null precursor/selected_ion; index files[]+entity_type chromatogram | unit | `dotnet test --filter Chromatogram` | ❌ Wave 0 |
| VER-01 | `mzpeak-validate` PASS 0/0 after chromatograms | smoke (shell-out) | reuse Phase 3 gate, extended | ✅ extend |
| VER-02 | differential vs mzml2mzpeak: nonzero (m/z,int) multiset equal per profile spectrum; counts/ms_level/polarity/RT/precursor | integration (pyarrow) | pyarrow script invoked from test or standalone | ❌ Wave 0 |
| VER-03 | NUnit: RAW→mzpeak, archive structure + chromatograms present + validator gate, skip-with-warning | integration | `dotnet test --filter MzPeak` | ✅ extend MzPeakWriterTests.cs |
| VER-04 | read-back L1 (m/z sorted/multiset) + L2 (intensity == f32 round-trip) | unit (pyarrow/NUnit) | pyarrow read-back | ❌ Wave 0 |

### Sampling Rate
- **Per task commit:** `dotnet test --filter MzPeak`
- **Per wave merge:** full `dotnet test`
- **Phase gate:** full suite green + `mzpeak-validate` PASS 0/0 before `/gsd:verify-work`

### Wave 0 Gaps
- [ ] Chromatogram facet assertions in `MzPeakWriterTests.cs` (covers CHROM-01/02)
- [ ] VER-02 differential pyarrow comparator (decode chunk, compare nonzero multisets) — script + NUnit driver
- [ ] VER-04 L1/L2 read-back assertions
- [ ] Skip-with-warning helper for absent `mzpeak-validate`/`python3.11`

## Security Domain

No network, no auth, no user input parsing, no crypto. Local file conversion only. ASVS categories
**not applicable** to this phase (data-format writer + offline verification). V5 input validation is
satisfied by the existing per-scan try/catch (`MzPeakSpectrumWriter.cs:216-221`).

## Sources

### Primary (HIGH confidence)
- `ThermoRawFileParser/Writer/MzMlSpectrumWriter.cs:917-1091` — `ConstructChromatograms`/`TraceToChromatogram` (TIC/base-peak API reuse)
- `ThermoRawFileParser/Writer/OntologyMapping.cs:1038-1135` — chromatogram CV terms (tic=MS:1000235, basepeak=MS:1000628)
- `ThermoRawFileParser/XIC/XicReader.cs:98-230` — `GetChromatogramData`/`TraceType`/`ChromatogramSignal` usage
- `ThermoRawFileParser/MainClass.cs:530-550` — `-f` format codes (4=mzPeak), `-p`=noPeakPicking
- `ThermoRawFileParser/Writer/MzPeakSpectrumWriter.cs` + `Writer/MzPeak/MzPeakParquet.cs` — Phase 3 writer/mechanism
- pyarrow dumps of `refs/mzPeak/small.unpacked.mzpeak/chromatograms_{data,metadata}.parquet` (schemas + values)
- `~/Claude/mzPeakValidator/mzpeak_validator/{core.py,profiles/mzpeak-0.9/rules/*.json,schema/tables/*}` — validator rules (no chromatogram structural rule)
- `~/Claude/mzML2mzPeak/README.md:86-150` + `--help` — forward CLI, `--no-numpress`, chunk layout, L1/L2
- Live runs: RAW→mzML→ref.mzpeak and RAW→trfp.mzpeak; chunk decode; nonzero-multiset equality

### Secondary (MEDIUM confidence)
- `~/Claude/mzML2mzPeak/knowledge/specs/mzPeak specification.md:69-75` — chunk layout + MS:1003089 delta encoding semantics

### Tertiary (LOW confidence)
- none

## Metadata

**Confidence breakdown:**
- TIC extraction (Decision 1): HIGH — exact API grepped + 1:1 alignment proven by pyarrow
- Ground-truth shapes (Decision 2): HIGH — direct pyarrow dump of reference
- Index/cv_list/validator (Decision 3): HIGH — read all rules + ran validator
- Differential pipeline (Decision 4): HIGH — ran end-to-end
- Comparison method (Decision 5): HIGH — chunk decode + equality verified
- NUnit env (Decision 6): HIGH — all tools probed live

**Research date:** 2026-06-14
**Valid until:** 2026-07-14 (stable; reference archive, validator, and converter are fixed local artifacts)
