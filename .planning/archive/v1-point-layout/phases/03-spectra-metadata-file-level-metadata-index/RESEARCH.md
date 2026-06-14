# Phase 3: Spectra Metadata + File-Level Metadata/Index - Research

**Researched:** 2026-06-14
**Domain:** mzPeak packed-parallel metadata tables (Parquet nested structs) + `mzpeak_index.json` metadata block, Thermo extraction reuse, CV mapping, mzpeak-validate conformance
**Confidence:** HIGH (every claim below is from a pyarrow dump of the reference archive, the validator profile source, or a file:line in the project tree)

## Summary

The "packed parallel tables" model in CONTEXT.md is **almost right but the per-row null
discipline is wrong**. A pyarrow dump of the reference `spectra_metadata.parquet` shows the four
top-level struct columns (`spectrum`, `scan`, `precursor`, `selected_ion`) are **four independent
tables co-resident in one file, each laid out at row position = that table's own ordinal, right-padded
with null-field rows to the shared file row count (48)**. They are NOT row-disjoint: row `i` carries
`spectrum[i]` AND `scan[i]` AND (if `i < #MS2`) `precursor[i]`/`selected_ion[i]`. Linkage is by the
`source_index` field, never by row position. The exact algorithm is in "Decision 1" below.

All Thermo extraction the phase needs already exists verbatim in `MzMlSpectrumWriter.cs` — one
method (`ConstructScanList`, line 2690) yields scan_start_time/filter_string/ion_injection_time/
scan_windows; `ConstructPrecursorList` (line 2355) yields isolation window + activation +
selected-ion; the spectrum-level block (line 1505-1674) yields polarity/TIC/base-peak/observed-mz.
`OntologyMapping` + the instrument-config loop (line 755) yield the instrument components. The plan
reuses these calls; it does NOT re-decode Thermo.

The validator is the decisive gate and resolving it is simpler than feared: **the official reference
archive itself FAILS `mzpeak-validate` with the same two errors**, one of which Phase 3 must avoid.
The metadata `meta_*_valid` rules read **Parquet footer KV blobs, not the index**, and self-gate on
absence. So Phase 3's only hard new requirement beyond the `scan` facet is **emitting `metadata.cv_list`
in the index** (declaring MS + UO), because the moment CV-named columns appear, `cv_list_declared`
fires if cv_list is absent.

**Primary recommendation:** Build the four struct columns as independent right-padded tables linked by
`source_index`; reuse the exact `MzMlSpectrumWriter` extraction calls; emit metadata blocks into BOTH
the index `metadata{}` AND the `spectra_metadata.parquet` footer KV (the footer is what the validator
schema-checks); always emit `metadata.cv_list` with MS + UO.

## Architectural Responsibility Map

| Capability | Primary Tier | Secondary Tier | Rationale |
|------------|-------------|----------------|-----------|
| Per-scan metadata extraction | Thermo RAW reader (`IRawDataPlus`) | `MzMlSpectrumWriter` helpers | The decode logic already lives in the mzML path; reuse, don't re-derive |
| Packed-parallel table assembly | `MzPeakSpectrumWriter` | `MzPeakParquet` helper | Phase-2 owns the per-ordinal loop and the low-level Parquet writer |
| Nested struct / list-of-struct Parquet | `MzPeakParquet` (Parquet.Net low-level) | — | POCO serializer cannot express nullable parallel struct columns |
| CV term → CURIE mapping | `OntologyMapping` | hard-coded constants for fixed terms | Instrument/dissociation dicts exist; representation/type/polarity are fixed literals |
| File-level metadata JSON | `MzPeakSpectrumWriter.BuildIndex` + new footer writer | `MetadataWriter`/`OntologyMapping` for instrument data | Index + footer are both written here |
| Conformance gate | `mzpeak-validate` (external) | NUnit | Validator profile is authoritative |

## User Constraints (from CONTEXT.md)

### Locked Decisions
- **Packed parallel tables** in one `spectra_metadata.parquet` = four nullable TOP-LEVEL struct columns
  (`spectrum`, `scan`, `precursor`, `selected_ion`). `scan`/`precursor`/`selected_ion` link back to their
  spectrum via `source_index`. **[RESEARCH CORRECTION — see Decision 1: the structs are NOT row-disjoint;
  each is an independent table right-padded to the shared row count. CONTEXT.md's "Each ROW populates
  exactly one struct; the other three are null" is contradicted by the reference archive.]**
- **Rich `spectrum` fields** (CV-accession column names) — confirmed exactly against the reference schema (Decision 3).
- **`scan`:** scan_start_time (UO_0000031 minute), filter_string, ion_injection_time, instrument_configuration_ref, scan_windows, source_index — confirmed.
- **`precursor`** (MSn only): isolation_window + activation PARAM list; **`selected_ion`** (MSn only): selected_ion_mz, charge_state, intensity, source_index — confirmed.
- **`mzpeak_index.json` metadata{}** block: instrument_configuration_list, software_list, data_processing_method_list, file_description, cv_list, sample_list/scan_settings_list — confirmed; **also emit into the parquet footer (Decision 4/6).**
- **CV mapping:** reuse `OntologyMapping` + `MzMlSpectrumWriter` extraction — confirmed available (Decision 2/5).

### Claude's Discretion
- Whether instrument metadata blocks go in index-only, footer-only, or both. **Recommendation: BOTH** (Decision 6 — the validator only checks the footer; the reference duplicates into both).
- Exact CV versions in cv_list (Decision 4 — recommend matching the validator pins to avoid the version warning).

### Deferred Ideas (OUT OF SCOPE)
- Chromatogram metadata facet (Phase 4).
- Row-group batching / large-RAW streaming (v2/OPT per Phase-2 SUMMARY).
- `ion_mobility_value`/`ion_mobility_type`, `mz_delta_model`, `auxiliary_arrays` (timsTOF / advanced — emit as null/empty; not in small.RAW).

## Phase Requirements

| ID | Description | Research Support |
|----|-------------|------------------|
| META-01 | `scan` facet (clears the validator error) | Decision 1 (algorithm) + Decision 2 (ConstructScanList reuse) + Decision 7 (columns.json contract) |
| META-02 | Rich `spectrum` fields | Decision 3 (exact schema + CV values) + spectrum-level reuse table |
| META-03 | `precursor` facet (MSn) | Decision 1 (precursor table mechanics) + ConstructPrecursorList reuse |
| META-04 | `selected_ion` facet (MSn) | Decision 1 + selected-ion reuse rows |
| META-05 | PARAM lists (activation, scan params, scan_windows) | PARAM shape (`MzPeakParquet.BuildParamField`) + Decision 3 CV table |
| IDX-01 | instrument_configuration_list | Decision 5 (OntologyMapping reuse) + Decision 6 (index+footer) |
| IDX-02 | software_list / data_processing_method_list / file_description | Decision 4 (templates) |
| IDX-03 | cv_list (MS, UO superset) | Decision 4 + Decision 6 (MANDATORY — validator `cv_list_declared`) |
| IDX-04 | sample_list / scan_settings_list (may be empty) | Decision 4 templates |

---

## DECISIONS (per unknown)

### Decision 1 — Ground-truth row mechanics & the EXACT row-emission algorithm

**Source:** pyarrow dump of `refs/mzPeak/small.unpacked.mzpeak/spectra_metadata.parquet` (48 rows). [VERIFIED: pyarrow 12.0.1]

**Layout (the corrected model):** Four independent struct columns, each an independent logical table
whose k-th entry sits at **row k**, right-padded with rows whose leaf fields are all null up to the file
row count = `max(table lengths)` = spectrum count = 48.

Measured null pattern (proper Arrow leaf-null check):
- `spectrum.index` non-null on rows **0..47** (48 entries — one per emitted ordinal).
- `scan.source_index` non-null on rows **0..47** (48 entries; `scan[i].source_index == scan[i].scan_index == i`).
- `precursor.source_index` non-null on rows **0..33** (34 entries = the 34 MS2 spectra); **null on rows 34..47**.
- `selected_ion.source_index` non-null on rows **0..33** (34 entries); null on 34..47.

> NOTE: a naive `to_pydict()` makes the struct look "present" on every row because Arrow returns a dict
> even when the entry is logically null. The leaf fields (`source_index` etc.) are the truth: they are
> null for the padded tail. Implement this as: write the precursor/selected_ion struct column with a
> definition-level array that is 0 (null) for rows >= 34.

**Linkage semantics (measured):**
- `spectrum.index = i` (dense 0-based ordinal — the Phase-2 join key). `id` = the Thermo nativeID string `controllerType=0 controllerNumber=1 scan=N`.
- `scan.source_index = i` (= row, = the spectrum it describes). `scan.scan_index = i` too.
- `precursor.source_index` = the **MS2 spectrum's** ordinal (the fragment scan). NOT the row. e.g. precursor row 0 has `source_index=2` (spectrum 2 is the first MS2). Sorted precursor.source_index == sorted list of MS2 spectrum indices exactly.
- `precursor.precursor_index` = the **MS1 parent spectrum's** ordinal (the survey scan the precursor was selected from). Measured distinct values {1, 8, 15, 22, 29, 35, 42} — each is an MS1 spectrum index. `precursor.precursor_id` = that MS1's nativeID. (Five consecutive MS2 share precursor_index=1 → all fragmented from MS1 spectrum index 1, i.e. scan=2.)
- `selected_ion.source_index` == `precursor.source_index` per row (both point at the same MS2 fragment spectrum); `selected_ion.precursor_index` == `precursor.precursor_index` per row.

**Representative rows (measured):**

| row | spectrum.index / ms_level / repr / type | scan.source_index / filter / rt(min) / iit | precursor.source_index / precursor_index / iso_target | selected_ion source_index / mz / charge |
|-----|------------------------------------------|--------------------------------------------|-------------------------------------------------------|------------------------------------------|
| 0 | 0 / 1 / MS:1000128 / MS:1000579 | 0 / `FTMS + p ESI Full ms [200.00-2000.00]` / 0.00494 / 68.23 | 2 / 1 / 810.789 | 2 / 810.789 / None |
| 1 | 1 / 1 / MS:1000128 / MS:1000579 | 1 / `ITMS + p ESI Full ms [200.00-2000.00]` / 0.00790 / 2.08 | 3 / 1 / 837.345 | 3 / 837.345 / None |
| 2 | 2 / 2 / MS:1000127 / MS:1000580 | 2 / `ITMS + c ESI d Full ms2 810.79@cid35.00 [210.00-1635.00]` / 0.01122 / 7.99 | 4 / 1 / 725.362 | 4 / 725.362 / None |
| 5 | 5 / 2 / MS:1000127 / MS:1000580 | 5 / `...ms2 558.87@cid35.00...` / 0.04862 / 66.48 | 9 / 8 / 810.753 | 9 / 810.753 / None |
| 7 | 7 / 1 / MS:1000128 / MS:1000579 | 7 / `FTMS + p ESI Full ms...` / 0.07502 / 61.13 | 11 / 8 / 644.057 | 11 / 644.057 / None |
| 33 | 33 / 2 / MS:1000127 / MS:1000580 | 33 / ... | 47 / 42 / ... | 47 / ... / None |
| 34..47 | 34..47 / (MS1 or MS2) / ... | 34..47 / ... / ... | **null** | **null** |

(Note row 0 is an MS1 spectrum but precursor row 0 describes a *different* spectrum (#2). The two tables are parallel, not aligned.)

**EXACT row-emission algorithm the writer must follow:**

```
Inputs from the Phase-2 loop, in emitted-ordinal order:
  spectra[]      : one entry per emitted ordinal i (0..N-1), each with ms_level, id, time, etc.
  N = spectra.Count   (= 48 for small.RAW)

1. Build the SPECTRUM table: N entries, entry i = spectrum metadata for ordinal i. (full def-level for all N rows)
2. Build the SCAN table: N entries, entry i = scan metadata for ordinal i,
     with scan.source_index = i, scan.scan_index = i. (full def-level for all N rows)
3. Build the PRECURSOR table: one entry per MS2 (ms_level >= 2) spectrum, in ascending ordinal order.
     For the k-th MS2 spectrum (whose ordinal is s, and whose MS1 parent ordinal is p):
         precursor[k].source_index    = s          (the MS2 fragment spectrum ordinal)
         precursor[k].precursor_index = p          (the MS1 parent ordinal)
         precursor[k].precursor_id    = id of spectrum p
         precursor[k].isolation_window / activation = from ConstructPrecursorList logic
   Let M = #MS2.  (M = 34 for small.RAW)
4. Build the SELECTED_ION table: one entry per MS2, SAME order as precursor, same source_index/precursor_index.
5. ROW COUNT = N (= max(N, N, M, M)).  Write all four as columns of one ParquetSchema with N rows:
     - spectrum column: def-level 1 for rows 0..N-1
     - scan column:     def-level 1 for rows 0..N-1
     - precursor column:    def-level 1 for rows 0..M-1, def-level 0 (null) for rows M..N-1
     - selected_ion column: def-level 1 for rows 0..M-1, def-level 0 (null) for rows M..N-1
```

The MS1-parent ordinal `p` is the most-recent MS1 spectrum before the MS2 in scan order — the
`MzMlSpectrumWriter` already tracks this via `_precursorScanNumbers[""]` / `_precursorTree`
(MzMlSpectrumWriter.cs:1336-1337). The plan can mirror that (track "last MS1 ordinal seen").

> **Pitfall:** precursor.source_index is the MS2 ordinal, precursor.precursor_index is the MS1 ordinal.
> Do not swap them. The selected_ion mirrors the precursor exactly.

---

### Decision 2 — Thermo extraction reuse table (the gold)

**Source:** `ThermoRawFileParser/Writer/MzMlSpectrumWriter.cs`. Every call below already exists; the plan
reuses it. [VERIFIED: file:line in repo]

| mzPeak column (target) | Thermo call / value | file:line |
|------------------------|---------------------|-----------|
| `scan.MS_1000016_scan_start_time` (minutes) | `_rawFile.RetentionTimeFromScanNumber(scanNumber)` | 2722 |
| `scan.MS_1000512_filter_string` | `scanEvent.ToString()` (the IScanEvent) | 2730 |
| `scan.MS_1000927_ion_injection_time` | `trailerData.AsDouble("Ion Injection Time (ms):")` | 1282 |
| `scan.scan_windows[0].MS_1000501_..lower` | `scanStat.LowMass` (`GetScanStatsForScanNumber`) | 2781 (stat src 1244) |
| `scan.scan_windows[0].MS_1000500_..upper` | `scanStat.HighMass` | 2791 |
| `scan.instrument_configuration_ref` | index of `scanFilter.MassAnalyzer` in `_massAnalyzers` (0-based; ref impl ground truth uses 0=FTMS,1=ITMS) | 2710 |
| `spectrum.MS_1000511_ms_level` | `(int)scanFilter.MSOrder` | 1250 |
| `spectrum.MS_1000465_scan_polarity` (int8 +1/-1) | `scanFilter.Polarity` (`PolarityType.Positive`→+1, `Negative`→-1) | 1506-1531 |
| `spectrum.MS_1000525_spectrum_representation` | `mzData.isCentroided` → MS:1000127 else MS:1000128 | 1587-1607 |
| `spectrum.MS_1000559_spectrum_type` | ms_level==1 → MS:1000579 else MS:1000580 | 1317-1360 |
| `spectrum.MS_1000285_total_ion_current` | `scanStats.TIC` | 1538 |
| `spectrum.MS_1000504_base_peak_mz` | `mzData.basePeakMass` | 1623 |
| `spectrum.MS_1000505_base_peak_intensity` | `mzData.basePeakIntensity` | 1638 |
| `spectrum.MS_1000528_lowest_observed_mz` | `mzData.masses[0]` (ascending) | 1612 |
| `spectrum.MS_1000527_highest_observed_mz` | `mzData.masses[last]` | 1613 |
| `spectrum.MS_1003060_number_of_data_points` (MS1/profile) | array length of the data facet for that ordinal | (Phase-2 accumulator) |
| `spectrum.MS_1003059_number_of_peaks` (MS2/centroid) | peak count for that ordinal | (Phase-2 accumulator) |
| `precursor.isolation_window.MS_1000827_target_mz` | `reaction.PrecursorMass` (`scanEvent.GetReaction(n)`) | 2375/2448 |
| `precursor.isolation_window.MS_1000828_lower_offset` | `isolationWidth.Value - offset`, `offset = width/2 + reaction.IsolationWidthOffset` | 2456-2462 |
| `precursor.isolation_window.MS_1000829_upper_offset` | `offset` | 2473 |
| `precursor.activation` PARAM: dissociation method | `OntologyMapping.DissociationTypes[reaction.ActivationType]` | 2500 |
| `precursor.activation` PARAM: collision energy | `reaction.CollisionEnergy` (when `reaction.CollisionEnergyValid`), unit UO:0000266 | 2485-2497 |
| `selected_ion.MS_1000744_selected_ion_mz` | `CalculateSelectedIonMz(reaction, monoisotopicMz, isolationWidth)` | 2392 |
| `selected_ion.MS_1000041_charge_state` (int32) | `trailerData.AsPositiveInt("Charge State:")` (null if absent — small.RAW has none) | 1280 |
| `selected_ion.MS_1000042_intensity` | `CalculatePrecursorPeakIntensity(...)` | 2420 |
| MS1-parent ordinal (precursor.precursor_index) | track "last MS1 scan seen" as `MzMlSpectrumWriter` does via `_precursorScanNumbers[""]` | 1336 |
| ion injection time / charge / monoiso source | `new ScanTrailer(_rawFile.GetTrailerExtraInformation(scanNumber))` | 1271 |

Supporting key methods to mine wholesale: `ConstructScanList(scanNumber, scanStat, scanFilter, scanEvent, monoisotopicMz, ionInjectionTime)` (2690-2803) and `ConstructPrecursorList(...)` (2355-2483). They already emit the exact CV accessions and units the mzPeak columns need.

---

### Decision 3 — CV value table

**Source:** pyarrow dump of reference `parameters`/`isolation_window`/`activation` + `OntologyMapping.cs`. [VERIFIED: pyarrow + file:line]

| Concept | mzPeak column / location | Form | Value(s) | Notes |
|---------|--------------------------|------|----------|-------|
| ms level | `spectrum.MS_1000511_ms_level` | **uint8 scalar** | 1, 2, ... | raw int, NOT a CURIE |
| polarity | `spectrum.MS_1000465_scan_polarity` | **int8 scalar** | **+1** (positive) / **-1** (negative) | confirmed: distinct value {1} in reference. NOT a CURIE. Maps from `PolarityType.Positive→+1`, `Negative→-1`. (mzML uses MS:1000130/129 strings; mzPeak collapses to a signed int8.) |
| representation | `spectrum.MS_1000525_spectrum_representation` | **CURIE string** | **MS:1000127** centroid / **MS:1000128** profile | reference MS1(FT profile)=MS:1000128, MS2(IT centroid)=MS:1000127. Reflects actual array continuity, NOT ms_level. |
| spectrum type | `spectrum.MS_1000559_spectrum_type` | **CURIE string** | **MS:1000579** (ms_level 1) / **MS:1000580** (ms_level >=2) | |
| lowest/highest obs mz | `spectrum.MS_1000528.._unit_MS_1000040`, `MS_1000527.._unit_MS_1000040` | double scalar, unit baked in column name (MS:1000040 m/z) | — | |
| base peak mz / intensity | `MS_1000504_..unit_MS_1000040` (double) / `MS_1000505_..unit_MS_1000131` (float) | scalar, unit in name | — | MS:1000131 = number of detector counts |
| total ion current | `MS_1000285_..unit_MS_1000131` | float scalar | — | |
| scan_start_time | `scan.MS_1000016_scan_start_time_unit_UO_0000031` | **float32 scalar**, unit UO:0000031 (minute) baked in name | — | see Decision 3b |
| ion injection time | `scan.MS_1000927_..unit_UO_0000028` | float32 scalar, unit UO:0000028 (ms) in name | — | |
| filter string | `scan.MS_1000512_filter_string` | large_string scalar | the filter | not CV-valued |
| scan window lower/upper | `scan.scan_windows[].MS_1000501_.._unit_MS_1000040`, `MS_1000500_..` | float32 scalar, unit in name | — | |
| isolation target/offsets | `precursor.isolation_window.MS_1000827_target_mz` / `MS_1000828_lower_offset` / `MS_1000829_upper_offset` | float32 scalars | reference offsets = 1.0/1.0 | NOTE: target column has NO `_unit_` suffix in reference. |
| dissociation method | `precursor.activation.parameters[]` PARAM entry | **PARAM with accession+name** | **CID MS:1000133**, **HCD MS:1000422** (beam-type CID), **ETD MS:1000598**, IRMPD MS:1000262, ECD MS:1000250, PQD MS:1000599, ETD-neg MS:1003247, UVPD MS:1003246, PTCR MS:1003249 | reference small.RAW uses **MS:1000133 "collision-induced dissociation"** — matches `OntologyMapping.DissociationTypes[CID]` exactly. (The mapping_report.md mislabels MS:1000133 as HCD — that is a report error; MS:1000133=CID, MS:1000422=HCD.) |
| collision energy | `precursor.activation.parameters[]` PARAM entry | **PARAM float value + unit** | accession **MS:1000045**, name "collision energy", value=35.0, **unit UO:0000266** (electronvolt) | confirmed in reference dump |
| selected ion mz | `selected_ion.MS_1000744_..unit_MS_1000040` | double scalar, unit in name | — | |
| charge state | `selected_ion.MS_1000041_charge_state` | **int32 scalar** | null in small.RAW | NOT CURIE |
| selected ion intensity | `selected_ion.MS_1000042_..unit_MS_1000131` | float scalar | — | |
| mass resolving power (example scan param) | `scan.parameters[]` PARAM | PARAM integer value | accession MS:1000800, value=100000 (reference) | optional; small.RAW has it. Plan may omit or carry if available. |

**Column-name convention:** built by `MzPeakParquet.CvColumn(accession, label, unitAccession)` → `MS_1000016_scan_start_time_unit_UO_0000031`. Verified helper exists (MzPeakParquet.cs:42). Note `isolation_window_target_mz` is the one column with NO unit suffix in the reference — match that.

### Decision 3b — Time unit gotcha (CONFIRMED: no conversion)

Reference `scan.MS_1000016_scan_start_time_unit_UO_0000031` is **float32, unit minute (UO:0000031)**.
`_rawFile.RetentionTimeFromScanNumber(scanNumber)` returns **minutes** (MzMlSpectrumWriter.cs:2722 emits
it with `unitAccession="UO:0000031" unitName="minute"` directly, no scaling). **No conversion needed —
emit the raw Thermo value as float32 with unit minute.** (Unlike mzML pipelines that sometimes store
seconds; TRFP and mzPeak both use minutes here.) [VERIFIED: file:line 2722-2725 + pyarrow column name]

---

### Decision 4 — `mzpeak_index.json` metadata{} block (exact JSON shapes)

**Source:** `cat refs/mzPeak/small.unpacked.mzpeak/mzpeak_index.json` + `refs/mzPeak/schema/*.json` + validator `schema/json/*.json`. [VERIFIED]

**Index top level:** `{ "version": "0.9", "files": [...], "metadata": { ... } }`. The validator index
schema **requires** `files` (each item: name, entity_type∈{spectrum,mass spectrum,chromatogram,wavelength spectrum,image,other}, data_kind∈{metadata,data arrays,peaks,other}) and `metadata` (object, **required `version`**).

> The reference puts `version` at index top level only; the validator reads `metadata.version`. Phase 1
> already writes `metadata.version` (MzPeakSpectrumWriter.cs:283-286). Keep it. The reference's absence of
> `metadata.version` is exactly why it FAILS `index_schema_valid` (Decision 6).

**Required keys per sub-schema** (from `refs/mzPeak/schema/` and validator `schema/json/`):
- `instrument_configuration` item requires: `id` (int), `components` (array), `parameters` (array), `software_reference` (string). Each component requires `component_type` ∈ {ionsource,analyzer,detector}, `order` (int), `parameters` (array).
- `data_processing_method`: properties `id`, `methods[]` (each: order, software_reference, parameters) — no top-level `required` declared.
- `software`: properties `id`, `version`, `parameters` — no `required` declared.
- `sample` requires: `id`, `name`, `parameters`.
- `ms_run` requires: `id`, `default_instrument_id` (int), `default_data_processing_id` (string), `default_source_file_id` (string); optional `start_time` (RFC3339), `parameters`.
- `file_description`: `contents[]` (PARAM), `source_files[]` (each requires id, name, location, parameters).
- `cv_list` item requires: **`id`, `version`, `uri`** (full_name optional).
- `param`: requires **`name`**; optional accession (CURIE pattern `\S+:\S+` or null), value, unit.

**Exact metadata{} template (drop-in for small.RAW, derived from reference + TRFP-available data):**

```json
{
  "version": "0.9",
  "cv_list": [
    {"id": "MS", "full_name": "PSI-MS controlled vocabulary", "version": "4.1.254",
     "uri": "https://raw.githubusercontent.com/HUPO-PSI/psi-ms-CV/master/psi-ms.obo"},
    {"id": "UO", "full_name": "Unit Ontology", "version": "2026-01-16",
     "uri": "http://purl.obolibrary.org/obo/uo.obo"}
  ],
  "file_description": {
    "contents": [
      {"accession": "MS:1000579", "name": "MS1 spectrum", "value": null, "unit": null},
      {"accession": "MS:1000580", "name": "MSn spectrum", "value": null, "unit": null}
    ],
    "source_files": [
      {"id": "RAW1", "name": "small.RAW", "location": "file://<dir>",
       "parameters": [
         {"accession": "MS:1000768", "name": "Thermo nativeID format", "value": null, "unit": null},
         {"accession": "MS:1000563", "name": "Thermo RAW format", "value": null, "unit": null},
         {"accession": "MS:1000569", "name": "SHA-1", "value": "<sha1>", "unit": null}
       ]}
    ]
  },
  "instrument_configuration_list": [
    {"id": 0, "software_reference": "ThermoRawFileParser",
     "parameters": [
       {"accession": "MS:1000448", "name": "LTQ FT", "value": null, "unit": null},
       {"accession": "MS:1000529", "name": "instrument serial number", "value": "<serial>", "unit": null}
     ],
     "components": [
       {"component_type": "ionsource", "order": 1,
        "parameters": [{"accession": "MS:1000073", "name": "electrospray ionization", "value": null, "unit": null}]},
       {"component_type": "analyzer", "order": 2,
        "parameters": [{"accession": "MS:1000079", "name": "fourier transform ion cyclotron resonance mass spectrometer", "value": null, "unit": null}]},
       {"component_type": "detector", "order": 3,
        "parameters": [{"accession": "MS:1000624", "name": "inductive detector", "value": null, "unit": null}]}
     ]},
    {"id": 1, "software_reference": "ThermoRawFileParser",
     "parameters": [
       {"accession": "MS:1000448", "name": "LTQ FT", "value": null, "unit": null},
       {"accession": "MS:1000529", "name": "instrument serial number", "value": "<serial>", "unit": null}
     ],
     "components": [
       {"component_type": "ionsource", "order": 1,
        "parameters": [{"accession": "MS:1000073", "name": "electrospray ionization", "value": null, "unit": null}]},
       {"component_type": "analyzer", "order": 2,
        "parameters": [{"accession": "MS:1000264", "name": "ion trap", "value": null, "unit": null}]},
       {"component_type": "detector", "order": 3,
        "parameters": [{"accession": "MS:1000253", "name": "electron multiplier", "value": null, "unit": null}]}
     ]}
  ],
  "software_list": [
    {"id": "ThermoRawFileParser", "version": "<MainClass.Version>",
     "parameters": [{"accession": "MS:1003145", "name": "ThermoRawFileParser", "value": null, "unit": null}]}
  ],
  "data_processing_method_list": [
    {"id": "trfp_conversion", "methods": [
      {"order": 0, "software_reference": "ThermoRawFileParser",
       "parameters": [{"accession": "MS:1000544", "name": "Conversion to mzML", "value": null, "unit": null}]},
      {"order": 1, "software_reference": "ThermoRawFileParser",
       "parameters": [{"accession": null, "name": "intensity narrowing", "value": "f64 to f32", "unit": null}]}
    ]}
  ],
  "run": {
    "id": "small", "default_instrument_id": 0,
    "default_data_processing_id": "trfp_conversion", "default_source_file_id": "RAW1",
    "start_time": "<rawFile.CreationDate RFC3339>"
  },
  "sample_list": [],
  "scan_settings_list": []
}
```

> **TRFP-vs-reference fidelity note:** TRFP's `OntologyMapping.MassAnalyzerTypes[ITMS]` = `MS:1000264 "ion
> trap"` (OntologyMapping.cs:28-34) whereas the pwiz-produced reference uses `MS:1000083 "radial ejection
> linear ion trap"`. TRFP's ionsource emits only `MS:1000073` (no `MS:1000057 electrospray inlet`). These
> are acceptable differences — the validator checks CV *resolvability* and structure, not exact instrument
> term identity. Use TRFP's own `OntologyMapping` outputs; do not hand-craft pwiz-identical terms.
> `sample_list`/`scan_settings_list` = `[]` is valid and simplest (TRFP has no sample metadata).

---

### Decision 5 — OntologyMapping → instrument_configuration reuse

**Source:** `OntologyMapping.cs` + `MzMlSpectrumWriter.cs:281-307, 755-870`. [VERIFIED: file:line]

| Need | Reuse | file:line |
|------|-------|-----------|
| instrument model CV from Thermo name | `OntologyMapping.GetInstrumentModel(instrumentData.Model)` (exact + longest-substring match) | OntologyMapping.cs:788; called MzMlSpectrumWriter.cs:292 |
| instrument model string + serial | `_rawFile.GetInstrumentData()` → `.Model`, `.SerialNumber` | MzMlSpectrumWriter.cs:283, 303 |
| analyzer CV per scan | `OntologyMapping.MassAnalyzerTypes[scanFilter.MassAnalyzer]` (FTMS→MS:1000079, ITMS→MS:1000264, TOF→MS:1000084, quad→MS:1000081, ...) | OntologyMapping.cs:15-90; used 860 |
| ionization CV | `OntologyMapping.IonizationTypes[scanFilter.IonizationMode]` (ESI→MS:1000073) | OntologyMapping.cs:95; used 772 |
| detector CV list per instrument | `OntologyMapping.GetDetectors(instrumentModel.accession)` (LTQ FT FT→inductive MS:1000624; IT→electron multiplier MS:1000253) | OntologyMapping.cs:857; used 869 |
| one config per distinct analyzer | loop over scans collecting `_massAnalyzers` (distinct `MassAnalyzerType` → "IC1","IC2",...), build a 3-component config (source order1 / analyzer order2 / detector order3) for each | MzMlSpectrumWriter.cs:755-880 |
| dissociation CV | `OntologyMapping.DissociationTypes[reaction.ActivationType]` | OntologyMapping.cs:220 |

The reference's two instrument_configurations (FTMS=id0, ion-trap=id1) correspond exactly to the two
distinct `MassAnalyzerType` values TRFP collects for small.RAW. `scan.instrument_configuration_ref`
(uint32) = the 0-based index of that scan's analyzer config. The Phase-3 plan should build the config
list with this loop and map each scan's `scanFilter.MassAnalyzer` to its config index.

---

### Decision 6 — Validator index/footer requirements (the decisive gate)

**Source:** ran `python3 -m mzpeak_validator refs/mzPeak/small.mzpeak` + read `mzpeak_validator/core.py` + profile `rules/*.json`. [VERIFIED: live validator run + source]

**The official reference archive FAILS the current validator with 2 errors + 1 warning:**
```
ERROR cv_list_declared    metadata.cv_list is absent/empty but the archive uses CV codes ['MS', 'UO']
ERROR index_schema_valid:metadata   schema violation at metadata: 'version' is a required property
WARNING profile_resolution          no mzpeak version declared; defaulted to latest profile
```

Implications for Phase 3:
1. **`metadata.version` is MANDATORY** (validator's bundled `schema/json/mzpeak_index.json` has `metadata.required:["version"]`). Phase 1 already emits it → our writer is AHEAD of the reference. Keep it. This also clears the `profile_resolution` warning.
2. **`metadata.cv_list` is MANDATORY** once any CV-named column exists. `cv_list_declared` (core.py:576) collects every CV code from inflected columns across spectra_metadata and requires each in `metadata.cv_list` (read from the **index**, path `metadata.cv_list`). Phase 3 introduces MS_* and UO_* columns → must declare MS and UO. cv_list items require `id`,`version`,`uri`. **Version policy (core.py:597-604):** warns ONLY if a declared version is NEWER than the profile pin (MS 4.1.254, UO 2026-01-16). Declare those exact versions (or older) → no warning.
3. **`columns_spectra_metadata` (the persisting Phase-2 error):** requires the `scan` facet present with `scan.source_index:uint`. Adding the scan struct clears it. (Decision 7.)
4. **`meta_*_valid` rules read PARQUET FOOTER KV blobs, not the index** (core.py:113 `footer()` → `pf.metadata.metadata[key]`; resolver core.py:371-375). They **self-gate: an absent footer blob is skipped** (core.py p_json_schema, not flagged). The reference duplicates all blocks into the spectra_metadata footer (keys: `run`, `software_list`, `sample_list`, `data_processing_method_list`, `instrument_configuration_list`, `file_description`, `scan_settings_list`, `spectrum_count`). spectra_data footer has `spectrum_array_index` (validated by `array_index_data_valid`).

**Mandatory metadata index keys (validator-enforced):** `version` (required), `cv_list` (required-when-CV-columns-present). **Everything else is optional** but recommended for fidelity.

---

### Decision 7 — spectra_metadata columns contract (what clears the error)

**Source:** validator `schema/tables/spectra_metadata.columns.json`. [VERIFIED]

```
spectrum  : required facet; required column index:uint;
            optional MS_1000511_ms_level(integer), MS_1000525_spectrum_representation(string),
                     MS_1003060_number_of_data_points(uint), MS_1003059_number_of_peaks(uint)
scan      : REQUIRED facet; required column source_index:uint    <-- the missing facet
precursor : optional facet; if present, source_index:uint required
selected_ion : optional facet; if present, source_index:uint required
```

`cv_inflection_spectra_metadata` (error/warn): every `${CV}_${ACC}_...` and `_unit_${CV}_${ACC}` column's
CV code must be pinned (MS, UO, IMS) and the accession must resolve in the bundled OBO (else warning).
Non-CV columns (`index`, `id`, `time`, `source_index`, `filter_string`, `data_processing_ref`, etc.) are skipped.
**Minimum to clear the gate:** emit `scan` with `source_index:uint`. Everything else in CONTEXT is fidelity.

---

## Standard Stack

### Core
| Library | Version | Purpose | Why Standard |
|---------|---------|---------|--------------|
| Parquet.Net | 5.0.1 (existing dep) | Low-level nested struct / list-of-struct / nullable parallel columns | Already used by `MzPeakParquet` helper; POCO serializer cannot express these |
| Newtonsoft.Json | existing dep | index + footer JSON | already used in `BuildIndex` |
| ThermoFisher.CommonCore | existing | RAW decode | the only RAW reader |

No new packages. **Package Legitimacy Audit: N/A — phase installs no external packages.**

### Parquet.Net capability verification (the primary risk)
- Phase-2 already writes a `StructField` with `DataField`s and uses def-level arrays (MzPeakSpectrumWriter.cs:215-230, def[]=1). So nullable struct columns via def-levels are **proven** in this codebase.
- **New for Phase 3:** (a) multiple top-level struct columns in ONE schema with DIFFERENT def-level arrays (spectrum/scan full, precursor/selected_ion padded-null); (b) nested struct-in-struct (`isolation_window`, `value`); (c) `large_list<struct>` (PARAM lists, scan_windows). The `MzPeakParquet.BuildParamField` already builds the PARAM `StructField` (MzPeakParquet.cs:25-38) but is **not yet wired into a list/write path** — Phase 3 must extend `WriteAsync`/`Column` to emit repeated (rep-level) list-of-struct columns. **This is the one genuine technical spike** (flagged in groundtruth_schema.md:137-144).
- `large_string`/`large_list` (64-bit offsets): reference uses Arrow large variants. Parquet.Net emits plain `string`/`list`; the existing Phase-2 metadata facet (plain `string` id) already validates, so plain variants are accepted by readers. **Confirm with a pyarrow round-trip read of our output** (verification step).

## Architecture Patterns

### Data flow
```
RAW scan loop (Phase-2 ordinal i)
  ├─> scanFilter / scanEvent / scanStats / trailer   (already read in Phase 2 loop)
  ├─> SPECTRUM table[i]      <- ms_level, polarity, repr, type, obs-mz, base peak, TIC, ndp/npk
  ├─> SCAN table[i]          <- rt, filter, iit, scan_windows, instrument_config_ref(=analyzer idx)
  └─ if ms_level>=2:
       PRECURSOR table[k]    <- source_index=i, precursor_index=lastMS1ordinal, iso window, activation PARAMs
       SELECTED_ION table[k] <- source_index=i, precursor_index=lastMS1ordinal, mz, charge, intensity
        (k = running MS2 counter)
  └─ track lastMS1ordinal = i  when ms_level==1

After loop:
  N rows. spectrum/scan def-level=1 for all N; precursor/selected_ion def-level=1 for k<M else 0.
  ParquetSchema(spectrum, scan, precursor, selected_ion) -> WriteFacet
  Footer KV: spectrum_count, run, instrument_configuration_list, software_list,
             data_processing_method_list, file_description, sample_list, scan_settings_list
  Index metadata{}: version, cv_list, + same blocks
```

### Anti-Patterns to Avoid
- **Row-disjoint structs (CONTEXT.md's stated model):** WRONG. Don't emit a row with only one struct populated. Use four independent right-padded tables.
- **Swapping precursor source_index/precursor_index:** source_index=MS2 ordinal, precursor_index=MS1 parent ordinal.
- **Omitting cv_list:** trips `cv_list_declared` the moment CV columns appear (the reference's own bug).
- **Hand-crafting pwiz-identical instrument terms:** use TRFP's `OntologyMapping`; structural conformance is what matters.
- **Converting RT to seconds:** Thermo RT is already minutes; emit raw with unit UO:0000031.

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Thermo scan/precursor/activation decode | custom filter/reaction parsing | `ConstructScanList` / `ConstructPrecursorList` (MzMlSpectrumWriter) | edge cases (SPS, supplemental activation, isolation offset math) already handled |
| Instrument model → CV | custom dict | `OntologyMapping.GetInstrumentModel/MassAnalyzerTypes/IonizationTypes/GetDetectors` | longest-substring matching + FT definition update already done |
| CV column naming | string concat | `MzPeakParquet.CvColumn` | unit-suffix convention proven |
| PARAM struct | new struct type | `MzPeakParquet.BuildParamField` + `MzPeakParam` | shape matches ground truth |
| last-MS1-parent tracking | scan re-scan | mirror `_precursorScanNumbers[""]` pattern | already the established approach |

## Runtime State Inventory

This is a writer/code change phase (extends `MzPeakSpectrumWriter`, no rename/migration). No stored
data, live service config, OS-registered state, secrets, or build artifacts carry phase-specific
identifiers. **None — verified: the only outputs are the `.mzpeak` archive (regenerated per run) and
NUnit tests; no persistent external state.**

## Common Pitfalls

### Pitfall 1: Treating the four structs as row-aligned
**What goes wrong:** Writing `precursor[i]` for the spectrum at row i.
**Why:** CONTEXT.md and intuition suggest one-row-one-spectrum-all-facets.
**How to avoid:** Build four independent tables; precursor table has M=#MS2 entries at rows 0..M-1, source_index=MS2 ordinal.
**Warning signs:** precursor.source_index == row index (should be the MS2 ordinal, which differs once MS1/MS2 interleave).

### Pitfall 2: Missing cv_list → new validator error
**What goes wrong:** Add scan facet, error count *changes* but a NEW `cv_list_declared` error appears.
**How to avoid:** Emit `metadata.cv_list` with MS + UO in the SAME plan task as the CV columns.

### Pitfall 3: list-of-struct repetition levels
**What goes wrong:** PARAM lists / scan_windows need Parquet repetition levels; the current `WriteAsync` only handles flat + def-level columns.
**How to avoid:** Spike the list-of-struct write path early; verify with a pyarrow read that `parameters`/`scan_windows`/`activation` round-trip.

### Pitfall 4: CV version warning
**What goes wrong:** Declaring MS version newer than 4.1.254 (or UO newer than 2026-01-16) emits a warning.
**How to avoid:** Declare versions matching the profile pins or older.

## Validation Architecture

### Test Framework
| Property | Value |
|----------|-------|
| Framework | NUnit (net8.0), via `ThermoRawFileParser/ThermoRawFileParser.sln` |
| Config file | `ThermoRawFileParserTest/MzPeakWriterTests.cs` (Phase-2, 7 tests) |
| Quick run | `dotnet test ... --filter MzPeakWriterTests` (build solution explicitly, not repo root — see Phase-2 deviation) |
| Full suite | `dotnet test ThermoRawFileParser/ThermoRawFileParser.sln` (34 tests at Phase-2 end) |
| Runtime | `~/.dotnet-x64` Rosetta x64 (`DOTNET_ROOT_X64`) |

### Phase Requirements → Test Map
| Req | Behavior | Test | Command |
|-----|----------|------|---------|
| META-01 | scan facet present, source_index=row | unit | `dotnet test --filter MzPeakWriterTests` |
| META-01..04 | per-struct def-level null discipline (precursor/selected_ion null on rows >= #MS2) | unit | same |
| META-03/04 | precursor.source_index==MS2 ordinals; precursor_index==MS1 parent ordinal | unit | same |
| META-02 | CV column names + values (polarity int8, repr/type CURIE) | unit | same |
| IDX-01..04 | instrument_config (2 configs), cv_list shape (MS+UO) | unit | same |
| gate | scan-facet error GONE; no new cv_list error | integration | `mzpeak-validate small.mzpeak` |
| diff | column names/struct shapes/CURIE forms vs reference | manual/pyarrow | `python3 -c "import pyarrow.parquet..."` |

### Sampling Rate
- Per task commit: `dotnet test --filter MzPeakWriterTests`
- Per wave merge: full solution test
- Phase gate: full suite green + `mzpeak-validate` shows scan error cleared and no new error, BEFORE `/gsd:verify-work`

### Wave 0 Gaps
- None — Phase-2 `MzPeakWriterTests.cs` exists; add new `[Test]` cases for the four-table null discipline and linkage. No new framework install.

## Environment Availability

| Dependency | Required By | Available | Version | Fallback |
|------------|------------|-----------|---------|----------|
| pyarrow | reference dump / output diff | ✓ | 12.0.1 | — |
| mzpeak-validate | decisive gate | ✓ | mzpeak-0.9, catalog 1.6 | — |
| dotnet x64 (Rosetta) | build/test | ✓ (per Phase-2) | net8.0 | — |
| Parquet.Net | nested write | ✓ | 5.0.1 | — |

No missing dependencies.

## State of the Art / Reference fidelity notes

| Reference behavior | Our (better) behavior |
|--------------------|-----------------------|
| No `metadata.version` → FAILS `index_schema_valid` | We emit it (Phase 1) — pass |
| No `cv_list` → FAILS `cv_list_declared` | Phase 3 must emit it — pass |
| Analyzer = MS:1000083 (pwiz) | We use MS:1000264 (TRFP OntologyMapping) — both resolve, structurally valid |

## Assumptions Log

| # | Claim | Section | Risk if Wrong |
|---|-------|---------|---------------|
| A1 | Parquet.Net 5.0.1 can write `large_list<struct>` with correct repetition levels via the low-level API | Standard Stack / Pitfall 3 | If not, PARAM/scan_windows lists need a workaround; spike de-risks this early |
| A2 | Declaring MS 4.1.254 / UO 2026-01-16 in cv_list resolves accessions cleanly (all our accessions exist in the pinned OBO) | Decision 4/6 | A stray accession not in the OBO → `cv_inflection` warning (not error); low risk |
| A3 | `instrument_configuration_ref` = 0-based analyzer-config index (reference: 0=FTMS,1=ITMS) | Decision 1/5 | Mis-index → wrong config link; verifiable against reference dump |
| A4 | Emitting metadata into BOTH index and footer is accepted (reference does footer; validator reads footer) | Decision 6 | Footer-only would also pass; index-only would NOT be schema-checked but also not flagged. Both = safest |
| A5 | charge_state null is acceptable (small.RAW has no Charge State trailer) | Decision 3 | Reference also null → safe |

## Open Questions

1. **List-of-struct repetition-level write in Parquet.Net 5.0.1.**
   - Known: flat + def-level columns work (Phase 2). PARAM struct shape exists (`BuildParamField`).
   - Unclear: exact rep-level array construction for `large_list<struct<...>>` in this Parquet.Net version.
   - Recommendation: first plan task = a write+pyarrow-read spike of a single `parameters` list column.

2. **Should `data_processing_ref` / `spectrum_reference` be populated or left null?**
   - Reference leaves both null. Recommendation: emit null (matches reference) but emit the `data_processing_method_list` block; wire refs in a later fidelity pass if validator ever requires.

## Sources

### Primary (HIGH confidence)
- pyarrow 12.0.1 dumps of `refs/mzPeak/small.unpacked.mzpeak/spectra_metadata.parquet`, `spectra_data.parquet` (schema, 48-row null pattern, linkage, PARAM/activation/isolation values, footer KV keys)
- `~/Claude/mzPeakValidator/mzpeak_validator/profiles/mzpeak-0.9/{profile.json, rules/*.json, schema/json/*.json, schema/tables/spectra_metadata.columns.json}` (conformance contract)
- Live run: `python3 -m mzpeak_validator refs/mzPeak/small.mzpeak` (the 2-error baseline)
- `ThermoRawFileParser/Writer/MzMlSpectrumWriter.cs` (lines 281-307, 755-880, 1244-1674, 2355-2483, 2690-2803)
- `ThermoRawFileParser/Writer/OntologyMapping.cs` (15-90, 95+, 220-323, 328+, 788-1035)
- `ThermoRawFileParser/Writer/{MzPeakSpectrumWriter.cs, MzPeak/MzPeakParquet.cs, MetadataWriter.cs}`
- `refs/mzPeak/small.unpacked.mzpeak/mzpeak_index.json`, `refs/mzPeak/schema/*.json`

### Secondary (MEDIUM confidence)
- `refs/_findings/mzpeak_groundtruth_schema.md`, `refs/_findings/mzpeak_mapping_report.md` (note: mapping_report mislabels MS:1000133 as HCD — corrected here against the live OntologyMapping + reference dump)

## Metadata

**Confidence breakdown:**
- Row mechanics / linkage: HIGH — direct pyarrow measurement of the reference
- Thermo reuse table: HIGH — file:line verified, calls already in production mzML path
- CV values: HIGH — measured in reference + cross-checked with OntologyMapping
- Validator requirements: HIGH — live run + source-read of every relevant rule
- Parquet.Net list-of-struct write: MEDIUM — capability inferred; needs the spike (Open Q1)

**Research date:** 2026-06-14
**Valid until:** 2026-07-14 (stable — local refs + pinned validator profile)
