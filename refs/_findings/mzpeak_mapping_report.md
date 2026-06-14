# mzML2mzPeak: Concrete Mapping Bible for C# ThermoRawFileParser Port

**Report Generated:** 2026-06-14  
**Scope:** Non-imaging plain mzML → mzPeak conversion (spectra + chromatograms). Imaging extension (imzML → mzPeak) and imaging-specific CV terms (IMS:1000050/51/52 coordinates) noted but excluded per user specification.

---

## A. Architecture Overview

### Design Intent
Convert arbitrary mzML mass-spectrometry LC-/GC-MS datasets (vendor-neutral: Thermo, Bruker, SCIEX, Agilent, Shimadzu, Waters) to a columnar **mzPeak** archive (ZIP of Apache Parquet facets + JSON index) **losslessly at canonical widths** — m/z as float64, intensity as float32 — while preserving all spectral and chromatographic information.

### Read Path
1. **Input:** Plain `.mzML` or `.mzML.gz` (non-imaging) via **mzdata 0.64.1** crate
   - `MZReaderType::<File, CentroidPeak, DeconvolutedPeak>::open_path()`
   - Iterates spectra + chromatograms independently
2. **Metadata discovery:** mzML headers (file_description, instrument_configuration, software, data_processing, sample_list, scan_settings)

### Write Path
1. **Output:** mzPeak archive via **mzpeak_prototyping** (git rev 29e59b24, HUPO-PSI/mzPeak)
   - `MzPeakWriterType::<File>::builder()` → build → write_spectrum / write_chromatogram → finish
2. **Schema:** Derived from sample spectrum arrays (m/z, intensity types) — no speculative widths; registered UNION of actual widths only
3. **Facets produced:**
   - `spectra_metadata.parquet` — per-spectrum metadata (scan time, MS level, precursor info, etc.)
   - `spectra_data.parquet` (profile/unknown) or `spectra_peaks.parquet` (centroid)
   - `chromatograms_metadata.parquet` + `chromatograms_data.parquet` (if chromatograms present)
   - `mzpeak_index.json` — file-level metadata + cv_list + transform record
4. **ZIP container** — all Parquet + index + optional optical images + optional SDRF/ISA metadata

---

## B. Authoritative Target Column Schema

### spectra_metadata.parquet
**Indexed table** with ONE row per spectrum. All columns nullable except `spectrum_index`.

| Column Name | Type | CURIE / Origin | Meaning |
|---|---|---|---|
| `spectrum_index` | uint64 | — | Ordinal spectrum position (0-based) |
| `spectrum_time` | float32 | — | Retention time (minutes, from mzML scan_start_time) |
| `scan.*` | struct | MS CV terms | Scan-event parameters (origin: mzML scan list) |
| `spectrum.ms_level` | uint8 | MS:1000511 | MS level (1, 2, 3, … — source 0 → 1 on write) |
| `spectrum.signal_continuity` | utf8 | MS:1000559 (profile) / MS:1000580 (centroid) | "profile", "centroid", or "unknown" |
| `spectrum.native_id` | utf8 | — | Original mzML spectrum ID (string, preserved verbatim) |
| `spectrum.polarity` | utf8 | MS:1000130 (positive) / MS:1000129 (negative) | "positive" or "negative" (optional, inferred from mzML) |
| `precursor.*` | struct (nested) | — | Precursor ion(s) for MS/MS; **only on MS2+** |
| — `mz` | float64 | MS:1000744 | Precursor m/z |
| — `charge` | uint8 | MS:1000041 | Charge state (optional) |
| — `intensity` | float32 | MS:1000042 | Precursor intensity (optional) |
| — `isolation_window` | struct | MS:1000828 (isolation window target m/z) | Target m/z ± tolerance (optional) |
| — — `target` | float64 | MS:1000828 | Isolation center m/z |
| — — `lower_offset` | float32 | MS:1000829 | Lower m/z boundary (width below target) |
| — — `upper_offset` | float32 | MS:1000830 | Upper m/z boundary (width above target) |
| — `activation` | struct | — | Fragmentation parameters (MS/MS only) |
| — — `method` | utf8 | MS:1000044 (CID) / MS:1000133 (HCD) / others | Dissociation method |
| — — `energy` | float32 | MS:1000045 | Collision energy (eV or %) |

**Key CVs for scan params** (emitted as nested struct fields):
- `MS:1000016` — scan_start_time (UO:0000031 minutes)
- `MS:1000512` — filter string (vendor-specific, optional)
- `MS:1000927` — ion injection time (UO:0000028 milliseconds, optional, Orbitrap-specific)

### spectra_data.parquet
**Chunked point table** — one row per m/z value in profile/unknown spectra. Schema derived from FIRST reconstructed spectrum.

| Column Name | Type | Unit | Meaning |
|---|---|---|---|
| `spectrum_index` | uint64 | — | Reference to spectrum in metadata |
| `spectrum_array_index` | uint32 | — | Ordinal index in the spectrum's m/z array (0-based) |
| `point.mz` | float64 | **UO:1000008** (m/z) | Mass-to-charge ratio (canonical width, ascending `sorting_rank: 0`) |
| `point.intensity` | float32 | **UO:0000269** (intensity unit) | Ion abundance (canonical width) |

**Notes:**
- **Canonical widths fixed:** m/z ALWAYS float64, intensity ALWAYS float32 — applied uniformly to every spectrum.
- **m/z widening** (f32→f64): exact, value-equal; no precision loss.
- **intensity narrowing** (f64→f32): lossy IF source is f64; flagged in data_processing provenance.
- **m/z sort-on-write:** ascending guaranteed before write (HUPO-PSI/mzPeak#23). Non-sorted source is reordered; already-sorted is byte-identical (common case for instrument output).
- **Chunking & compression:** ZSTD level 19 (default), Numpress-linear m/z optional (lossy, records transform CURIE if applied).

### spectra_peaks.parquet
**Centroid facet** — one row per centroid peak. Only written when source signal_continuity == Centroid.

| Column Name | Type | Unit |
|---|---|---|
| `spectrum_index` | uint64 | — |
| `spectrum_array_index` | uint32 | — |
| `peak.mz` | float64 | m/z |
| `peak.intensity` | float32 | intensity units |

**Notes:**
- m/z is ALWAYS float64 (even if source is f32; widened in peaks facet per upstream schema constraint).
- Intensity ALWAYS float32.
- Peaks are **explicitly constructed from sorted m/z+intensity pairs** (no reordering in facet itself; array already sorted upstream).

### chromatograms_metadata.parquet
**Indexed table** — one row per chromatogram (TIC, BPC, XIC traces, SRM chromatograms).

| Column Name | Type | Meaning |
|---|---|---|
| `chromatogram_index` | uint64 | Ordinal chromatogram position |
| `chromatogram.id` | utf8 | Original mzML chromatogram ID |
| `chromatogram.type` | utf8 | "TIC", "BPC", "XIC", or vendor-specific trace type |
| `chromatogram.polarity` | utf8 | "positive" or "negative" (optional) |

### chromatograms_data.parquet
**Point table** — one row per retention-time / intensity pair.

| Column Name | Type | Unit |
|---|---|---|
| `chromatogram_index` | uint64 | — |
| `chromatogram_array_index` | uint32 | — |
| `point.time` | float32 | **UO:0000031** (minutes) |
| `point.intensity` | float32 | intensity units |

**Notes:**
- Time is float32 (canonical).
- Intensity is float32.
- **Empty chromatogram fallback:** If source mzML has zero chromatograms, the writer registers one empty chromatogram (`ChromatogramDescription::default()`) so the metadata facet exists and readers don't error on missing facet at open time.

---

## C. CONCRETE mzML CV → mzPeak Mapping Table

### **MS-Level & Spectrum Classification**

| mzML Concept | Source mzML Element | mzML CV Accession | mzPeak Target | Column / Block | Mapping Logic |
|---|---|---|---|---|---|
| MS level | `spectrum[@index]` (hierarchy) or `cvParam@accession="MS:1000511"` | MS:1000511 | `spectrum.ms_level` (uint8) | spectra_metadata | Source level passed through; if absent or `0` → rewrite to `1` (MS1) via **single-source policy** |
| Spectrum type (MS1 vs MSn) | Usually inferred from MS level | MS:1000579 (MS1) / MS:1000580 (MSn) | `spectrum.signal_continuity` | spectra_metadata | EXPLICIT emit: ms_level 0–1 → MS:1000579; ms_level ≥2 → MS:1000580 (prevents writer's per-spectrum inference log spam) |
| Signal continuity (profile vs centroid) | `binaryDataArrayList` array count & type | MS:1000559 (profile) / MS:1000511 (centroid) | `spectrum.signal_continuity` + routing to facet | spectra_metadata + facet choice | Derived from Representation (Profile/Centroid/Unknown); routes to `spectra_data` (profile/unknown) or `spectra_peaks` (centroid) |
| Polarity | `polarity` attribute or `cvParam@accession="MS:1000130"` / `MS:1000129` | MS:1000130 (positive) / MS:1000129 (negative) | `spectrum.polarity` (utf8) | spectra_metadata | Optional; mapped to "positive" or "negative" string; passed through verbatim from mzML |
| Retention time (scan_start_time) | `scan>cvParam@accession="MS:1000016"@value` | MS:1000016 | `spectrum_time` (float32, minutes) | spectra_metadata | Source time value; units implicitly UO:0000031 (minutes) in mzML; stored as-is in mzPeak |
| Ion injection time | `scan>cvParam@accession="MS:1000927"@value` | MS:1000927 | `scan_start_time_unit_*` (if present in scan struct) | spectra_metadata | Optional Orbitrap parameter; if present, mapped to scan param with unit UO:0000028 (milliseconds) |
| Filter string | `scanList>scan>cvParam@accession="MS:1000512"@value` | MS:1000512 | `scan.filter_string` (if included in scan struct) | spectra_metadata | Vendor-specific instrument filter string (Thermo, Waters, etc.); preserved as optional scan param |

### **Precursor & Selected Ion Parameters (MS/MS)**

| mzML Concept | Source mzML Element | mzML CV Accession | mzPeak Target | Mapping Logic |
|---|---|---|---|---|
| Precursor m/z | `precursor>selectedIonList>selectedIon>cvParam@accession="MS:1000744"` | MS:1000744 | `precursor.mz` (float64) | Extracted verbatim; first selected ion used if multiple |
| Precursor charge | `precursor>selectedIonList>selectedIon>cvParam@accession="MS:1000041"` | MS:1000041 | `precursor.charge` (uint8) | Optional; extracted and stored; zero/absent → not recorded |
| Precursor intensity | `precursor>selectedIonList>selectedIon>cvParam@accession="MS:1000042"` | MS:1000042 | `precursor.intensity` (float32) | Optional; extracted from mzML |
| Isolation window target m/z | `precursor>isolationWindow>cvParam@accession="MS:1000828"` | MS:1000828 | `precursor.isolation_window.target` (float64) | Extracted verbatim |
| Isolation window lower offset | `precursor>isolationWindow>cvParam@accession="MS:1000829"` | MS:1000829 | `precursor.isolation_window.lower_offset` (float32) | Width (m/z units) below target; optional |
| Isolation window upper offset | `precursor>isolationWindow>cvParam@accession="MS:1000830"` | MS:1000830 | `precursor.isolation_window.upper_offset` (float32) | Width above target; optional |
| Activation method (dissociation) | `precursor>activation>cvParam@accession="MS:1000044"` (CID) / `MS:1000133` (HCD) / others | MS:1000044 (CID) / MS:1000133 (HCD) / MS:1000135 (ECD) / … | `precursor.activation.method` (utf8, CURIE string) | Dissociation type extracted; multiple methods possible (rare) |
| Collision energy | `precursor>activation>cvParam@accession="MS:1000045"@value` | MS:1000045 | `precursor.activation.energy` (float32) | Fragmentation energy (eV or relative %); units implicit in mzML, stored as-is |

### **Data Array Parameters (m/z & Intensity)**

| mzML Concept | Source mzML Element | mzML CV Accession | mzPeak Target | Mapping Logic |
|---|---|---|---|---|
| m/z array | `binaryDataArrayList>binaryDataArray[name='m/z array']` | MS:1000514 (m/z array) | `point.mz` column | Decoded to float64 (canonical); source f32 → f64 widening (exact); source f64 unchanged |
| Intensity array | `binaryDataArrayList>binaryDataArray[name='intensity array']` | MS:1000515 (intensity array) | `point.intensity` column | Decoded to float32 (canonical); source f64 → f32 narrowing (lossy if occurs); source f32 unchanged |
| Detector counts unit | Implied by CV term MS:1000515 | — | UO:0000269 (arbitrary intensity unit) | Implicit; recorded as unit on intensity column in index |
| Time array (chromatogram) | `binaryDataArrayList>binaryDataArray[name='time array']` | MS:1000595 (time array) | `point.time` column | Decoded as float32; units UO:0000031 (minutes) |

### **File-Level Metadata (cv_list, File Description)**

| mzML Concept | Source mzML Element | Accession | mzPeak Target | Mapping Logic |
|---|---|---|---|---|
| Controlled vocabularies | `<cvList>` (MS, UO, IMS optional) | — | `metadata.cv_list` (array of {id, full_name, uri, version}) | Single-source registry (cv.rs cv_entry_for()); MS, IMS, UO always emitted; versions/URIs from upstream mzpeak_prototyping::param::ControlledVocabularyEntry |
| File description content | `<fileDescription><fileContent><cvParam>` | MS:1000579 (MS1 spectrum) / MS:1000580 (MSn spectrum) / etc. | `metadata.file_description.contents` | Copied verbatim from source mzML |
| Source files | `<fileDescription><sourceFileList><sourceFile>` | — | `metadata.file_description.source_files` | Verbatim: id, name, location, file format, ID format (if declared) |
| Software list | `<softwareList><software>` | — | `metadata.software_list` | Verbatim: id, version, CV params (name, type, vendor, etc.) |
| Data processing methods | `<dataProcessingList><dataProcessing><processingMethod>` | — | `metadata.data_processing_method_list` | Verbatim: order, software_ref, CV params (e.g., centroiding, deisotoping, filtering) |
| Sample list | `<sampleList><sample>` | — | `metadata.sample_list` | Verbatim: id, name, CV params |
| Instrument configuration | `<instrumentConfigurationList><instrumentConfiguration>` | — | `metadata.instrument_configuration_list` | Verbatim: id, software_ref, components (ion source, analyzer, detector), CV params |
| Scan settings (non-imaging) | `<scanSettingsList><scanSettings>` | — | `metadata.scan_settings_list` | Copied if present; typically contains instrument-specific scan parameters |
| MS run | `<run>` | — | `metadata.run` | Derived from spectrum iteration; id from @id attribute |

### **Provenance & Transformation Records (Data Processing)**

| Event | Source | Target | CV Accession | Notes |
|---|---|---|---|---|
| **m/z sort-on-write** | Non-monotonic source m/z detected | Data-processing step | — | Emitted as `mzml2mzpeak_sort_peaks` when ≥1 spectrum reordered |
| **Intensity narrowing (f64→f32)** | Source intensity is f64; canonicalized to f32 | Data-processing step | — | Emitted as `mzml2mzpeak_intensity_narrowing` if any spectrum's intensity narrowed |
| **Numpress-linear m/z** | `--no-numpress` not set (default lossy) | `metadata.transform` + data_processing | MS:1002312 | Recorded if Numpress-linear compression applied; lossy m/z encoding. Index's array `transform` field also carries MS:1002312 CURIE |
| **No compression** | `--no-numpress` flag set | spectra_data facet | — | m/z stored as lossless Delta-encoded float64; bit-exact round-trip (L1 conformance) |

---

## D. Write Path Summary: Array Handling & Layout Decisions

### **Canonical Width Rules** ⚡
**CRITICAL for C# port:**

1. **m/z column:**
   - Source dtype: read as-is from mzML (typically float32 or float64)
   - **Target dtype (ALWAYS):** float64
   - **Transform:** f32 → f64 via `as_f64()` (widening, exact, no loss)
   - **Stored in:** `spectra_data.point.mz` (profile/unknown) and `spectra_peaks.peak.mz` (centroid)
   - **Narrowing:** NEVER occurs (f32→f64 is exact; f64→f64 unchanged)

2. **Intensity column:**
   - Source dtype: read as-is from mzML (typically float32 or float64)
   - **Target dtype (ALWAYS):** float32
   - **Transform:** f32 → f32 (no change); f64 → f32 via `intensity_as_f32()` (narrowing, **lossy**)
   - **Stored in:** `spectra_data.point.intensity` and `spectra_peaks.peak.intensity`
   - **Narrowing flag:** IF source intensity is f64, set `CastNarrowing::intensity_f64_to_f32 = true` → emit data_processing provenance step + CLI warning

3. **Time column (chromatogram):**
   - **Target dtype:** float32
   - **Units:** UO:0000031 (minutes)

### **Per-Spectrum Schema Registration**
**Source:** First reconstructed spectrum's `raw_arrays()` BinaryArrayMap  
**Process:**
1. Call `peak_series::array_map_to_schema_arrays()` on sample arrays
2. Extract `(array_type, dtype, unit)` tuple for each array
3. Register only the UNION of widths **actually present in sampled spectra**
4. **Never speculate:** no second m/z column at a different dtype (array_buffer.rs:356 panics on speculative widths due to zero-intensity masking)

**Example:**
- Sample spectrum: m/z=float64, intensity=float32 → register ONE m/z column (float64) + ONE intensity column (float32)
- Writer applies this schema uniformly to ALL spectra (no per-spectrum derived width variation)

### **m/z Sort-on-Write**
**Goal:** Guarantee `point.mz` column is ascending (mzPeak spec `sorting_rank: 0` + Parquet range index requirement).

**Logic:**
1. Observe the m/z axis the writer will consume:
   - If centroid: m/z from picked peak set (if present), else raw array
   - If profile: raw array
2. Check if m/z is already non-decreasing (`partial_cmp` on float64):
   - **YES (common):** return clones unchanged → **byte-identical output**
   - **NO (instrument anomaly or ion-mobility unstack):** 
     - Compute stable argsort permutation (ascending)
     - Reorder both m/z and intensity arrays in-place via permutation
     - Drop any pre-decoded peak set (becomes stale)
     - Emit `mzml2mzpeak_sort_peaks` data_processing step (once per file if any reordered)
     - For centroid: count non-monotonic and warn
3. **Non-finite m/z handling:** NaN/±∞ on profile allowed (orders via `partial_cmp().unwrap_or(Equal)`). NaN on centroid rejected at boundary (CR-02 error).

### **Centroid vs Profile Routing**
- **Representation::Profile** → NO peak list attached → writer routes to `spectra_data`
- **Representation::Centroid** → EXPLICIT `CentroidPeak` peak list constructed (`peak.mz` f64 + `peak.intensity` f32`) → writer routes to `spectra_peaks`
- **Representation::Unknown** → NO peak list → routes to `spectra_data`

**Why explicit peak list for centroid?** Reference writer's peaks facet only recognizes canonical `CentroidPeak` schema (m/z f64 + intensity f32 per mzpeaks crate). Raw arrays with dtype-suffixed names (`mz_f64`/`intensity_f32`) don't map into peaks facet, so values serialize as NULL without the peak list.

### **Precursor Packing (MS/MS)**
- **Read:** Via mzdata's `MultiLayerSpectrum::precursors()` (flat list)
- **Write:** Nested struct `precursor` (first precursor; multiple precursors compressed to one in mzPeak v0.9)
  - `.mz`, `.charge`, `.intensity` — top-level fields
  - `.isolation_window` — nested struct: `.target`, `.lower_offset`, `.upper_offset`
  - `.activation` — nested struct: `.method` (utf8 CURIE string), `.energy` (f32)

**Isolation window:** `lower_offset` and `upper_offset` are WIDTHS (not absolute bounds); target ± offsets give the full window.

---

## E. File-Level Metadata Mapping

### **Minimal Required Set**
Every mzPeak archive emits:
1. **file_description** (from mzML source)
   - contents: MS1/MSn spectrum type parameters
   - source_files: original input file(s)
2. **cv_list** (base: MS, IMS, UO; imaging adds imaging CV accessions)
3. **software_list** (from mzML; mzml2mzpeak software entry added)
4. **data_processing_method_list** (from mzML; mzml2mzpeak processing steps appended)
5. **instrument_configuration_list** (from mzML)
6. **sample_list** (from mzML, optional)
7. **scan_settings_list** (from mzML, optional; non-imaging path rarely has this)

### **Provenance Injection**
At write time, a new `Software` entry is created:
- **id:** `mzml2mzpeak`
- **version:** (tool version, e.g., "0.9.0")
- **params:** mzml2mzpeak version CV param (if defined)

And a new `DataProcessing` entry:
- **id:** `mzml2mzpeak_processing`
- **methods:** 
  - Always: `mzml2mzpeak_convert` (minimal processing method, software_ref → `mzml2mzpeak`)
  - Conditionally: `mzml2mzpeak_sort_peaks` (if any spectrum m/z reordered)
  - Conditionally: `mzml2mzpeak_intensity_narrowing` (if any intensity f64→f32)
  - Conditionally: `mzml2mzpeak_numpress_linear` (if Numpress-linear m/z applied)

### **Transform Record** (optional)
**Emitted in `metadata.transform` (file-level JSON block) IF:**
- Numpress-linear m/z chunking is applied (lossy option)
- **Content:**
  ```json
  {
    "transform": "MS:1002312",  // numpress_linear_curie()
    "data_processing_ref": "mzml2mzpeak_numpress_linear",
    "conformance_level": "L2"  // L1 is strict; L2 allows bounded-error transform
  }
  ```

---

## F. Chromatogram Handling

### **Chromatogram Types**
Common types written to mzPeak:
- **TIC** (Total Ion Current) — all m/z summed per scan
- **BPC** (Base Peak Current) — maximum intensity per scan
- **XIC** (Extracted Ion Chromatogram) — m/z window sum
- **SRM/MRM** (Selected Reaction Monitoring) — precursor→product trace

### **Write Path**
1. Iterate source mzML chromatograms via `reader.iter_chromatograms()`
2. For each: call `writer.write_chromatogram(&chrom)`
3. Schema derived from sample (first 10 chromatograms) via `sample_array_types_from_chromatograms()`
4. Arrays: time (float32, minutes) + intensity (float32)

### **Empty Chromatogram Fallback**
**Critical:** If source mzML has **zero chromatograms:**
- Writer still registers an empty `Chromatogram` (default description, empty arrays)
- Call `write_chromatogram(&empty)`
- **Why:** Reference reader loads chromatogram metadata facet at open time and errors if facet is absent (EVG conformance issue)
- **Result:** Archive opens cleanly in readers; metadata facet exists; no data rows (safe)

### **Metadata per Chromatogram**
- **id** (utf8): original mzML chromatogram ID
- **type** (utf8): "TIC", "BPC", "XIC", "SRM", or vendor-specific
- **polarity** (optional utf8): "positive" or "negative"

---

## G. Conformance & Gotchas

### **1. Sorting Requirement (HUPO-PSI/mzPeak#23)**
- **Spec requirement:** `point.mz` column declared `sorting_rank: 0` (ascending)
- **Parquet range index:** requires actual ascending order; unsorted column silently breaks m/z range slices downstream
- **Converter behavior:** Sort-on-write GUARANTEES ascending; reorder cost is negligible (no-op for already-sorted instrument output)
- **Conformance level:** Automatic (L1 enforcement, not optional)

### **2. Intensity Narrowing Warning (DTY-03)**
- **Source f64, target f32:** lossy cast; flagged with `CastNarrowing::intensity_f64_to_f32 = true`
- **Emitted signals:**
  - Data-processing step: `mzml2mzpeak_intensity_narrowing`
  - CLI warning: "Intensity narrowed from Float64 to Float32; precision loss possible"
  - Archive: transform record notes the loss (if L2 conformance selected)
- **Impact:** L1 conformance requires no loss; L2 tolerates bounded error under transform

### **3. m/z Widening Safety (no regression)**
- **Source f32 → target f64:** every f32 is exactly representable in f64; no precision loss
- **Safety:** m/z NEVER narrows; bitwise f32 → f64 conversion is exact

### **4. Absent MS Level → MS1 (Single-Source Policy)**
- **Root cause:** mzML may declare `ms_level="0"` or omit MS:1000511 entirely
- **Canonical rule:** 0 or missing → emit as MS1 (1) on mzPeak side
- **Applied at:** 
  - Imaging spectrum builder (src/write/spectrum.rs line 95: `ms_level_or_ms1()`)
  - Plain mzML path (src/write/mzml.rs line 308)
  - Imaging-index accumulator (for MS1 m/z bounds logic)
- **Consistency:** Always ONE place; no drift-by-construction

### **5. CV Governance (CVL-01, CVL-02)**
- **cvL-01: Single source of truth** — `src/schema/cv.rs::cv_entry_for()` is the ONE registry for every CV (id, full_name, uri, version)
  - No independent CV literals elsewhere
  - Reverse imzML writer reads from `cv_list()` (same source)
  - No-drift-by-construction (enforced by test `no_drift_reverse_cvlist_reads_cv_list`)
- **CVL-02: Declared ⊇ Referenced** — Every CV accession in the archive is declared in `metadata.cv_list`
  - File-level index declares MS, IMS (imaging), UO, UNIMOD (sample metadata), mzml2mzpeak (local)
  - Readers can resolve all CURIEs

### **6. Numpress-Linear m/z Compression (Optional Lossy)**
- **Default:** Lossy Numpress-linear m/z (ZSTD compression level 19)
- **Opt-out:** `--no-numpress` flag → lossless Delta-encoded float64 (larger, bit-exact)
- **Transform record:** If lossy, `metadata.transform` records MS:1002312 CURIE + data_processing_ref
- **Conformance:**
  - L1 (strict): no lossy compression; Numpress files fail L1 check
  - L2 (bounded): Numpress allowed; relative m/z error ≤ 1e-7, recorded in archive

### **7. Centroid vs Profile (No Inference)**
- **Explicit:** `signal_continuity` set from `Representation` (never inferred from array count)
  - Profile → spectra_data
  - Centroid → spectra_peaks (with explicit CentroidPeak peak list)
  - Unknown → spectra_data
- **Reason:** Prevents writer log spam ("Couldn't infer spectrum type") on large batches

### **8. Peak List Routing (Centroid Peaks Facet)**
- **Centroid MUST have explicit peak list** for writer to populate peaks facet
- **Raw arrays alone insufficient:** writer reads `RefPeakDataLevel::Centroid(_)` branch only if peak list present
- **Source f32 m/z:** peak list widens to f64 (upstream schema constraint); raw array stays f32 (data facet)

### **9. Ion-Mobility Unstack (timsTOF)**
- **Detection:** `entry.has_ion_mobility_dimension()` → 3D array with mobility axis
- **Sort-on-write:** Stack 3D arrays → unstack (reorder by m/z) → return
- **Reason:** Mobility spectra often arrive unsorted in m/z; unstack preserves mobility data while sorting

### **10. Coordinate Validation (Imaging, WR-03)**
- Non-imaging path: N/A
- Imaging path: Coordinates x, y, z must be **positive 1-based pixel indices** (≥1)
- Error if any < 1 (non-positive rejected with typed WriteError)

---

## H. Key File-Level Constants & CVs Emitted

### **Controlled Vocabulary List** (Always emitted)
```
[
  {
    "id": "MS",
    "full_name": "Proteomics Standards Initiative Mass Spectrometry Ontology",
    "uri": "http://purl.obolibrary.org/obo/ms/4.1.248/ms.obo",
    "version": "4.1.248"
  },
  {
    "id": "IMS",
    "full_name": "Imaging Mass Spectrometry Ontology",
    "uri": "https://raw.githubusercontent.com/imzML/imzML/refs/heads/master/imagingMS.obo",
    "version": "1.1.0"
  },
  {
    "id": "UO",
    "full_name": "Units of measurement ontology",
    "uri": "http://purl.obolibrary.org/obo/uo/releases/2026-01-16/uo.obo",
    "version": "2026-01-16"
  }
]
```

### **Core MS Accessions** (Non-Exhaustive)

| Accession | Term | Usage |
|---|---|---|
| MS:1000511 | MS level | Spectrum hierarchy (1, 2, 3, …) |
| MS:1000514 | m/z array | Array identifier |
| MS:1000515 | intensity array | Array identifier |
| MS:1000516 | wavelength array | Wavelength spectra (not in plain mzML) |
| MS:1000527 | data processing applied | Data processing list |
| MS:1000559 | profile spectrum | Signal continuity (explicit emit) |
| MS:1000580 | centroid spectrum | Signal continuity (explicit emit) |
| MS:1000744 | selected ion m/z | Precursor m/z |
| MS:1000041 | charge state | Precursor charge |
| MS:1000042 | intensity | Selected ion intensity |
| MS:1000045 | collision energy | Activation energy (MS/MS) |
| MS:1000044 | CID | Dissociation method |
| MS:1000133 | HCD | Dissociation method |
| MS:1000828 | isolation window target m/z | Precursor isolation window |
| MS:1000829 | isolation window lower offset | Isolation lower width |
| MS:1000830 | isolation window upper offset | Isolation upper width |
| MS:1002312 | Numpress linear m/z compression | Transform CURIE (lossy option) |
| MS:1000016 | scan start time | Retention time |
| MS:1000927 | ion injection time | Orbitrap-specific param |
| MS:1000512 | filter string | Vendor instrument filter |

### **Unit Ontology (UO) Accessions**

| Accession | Term | Context |
|---|---|---|
| UO:0000031 | minute | Retention time, chromatogram time |
| UO:0000028 | millisecond | Ion injection time |
| UO:1000008 | m/z | m/z array unit |
| UO:0000269 | arbitrary intensity unit | Intensity array unit (default) |

---

## I. Summary: Implementation Checklist for C# Port

### **Must Implement**
- [x] Read mzML via vendor library (e.g., ThermoRawFileParser's mzML reader)
- [x] Extract spectrum hierarchy (MS level, signal continuity)
- [x] Decode m/z + intensity arrays from binary data blocks
- [x] Canonical-width coercion: m/z → float64, intensity → float32
- [x] m/z sort-on-write (ascending guarantee; reorder if needed)
- [x] Precursor info (m/z, charge, isolation window, activation)
- [x] Chromatogram iteration (time + intensity arrays)
- [x] Parquet schema from sample arrays (no speculative widths)
- [x] Build Parquet facets: spectra_metadata, spectra_data/peaks, chromatograms_*
- [x] ZIP container + mzpeak_index.json
- [x] File-level metadata mapping: software, instrument, sample, data_processing, cv_list
- [x] Provenance: mzml2mzpeak software entry + processing methods

### **Must NOT Do**
- [ ] Infer signal continuity from array count (explicit from Representation)
- [ ] Skip peak list for centroid spectra (explicit CentroidPeak needed)
- [ ] Omit chromatogram facet if zero chromatograms (register one empty)
- [ ] Allow unsorted m/z (sort-on-write is mandatory)
- [ ] Speculate spectrum-specific widths (register only actual widths)
- [ ] Emit independent CV literals (single registry in cv.rs equivalent)

### **Optional Features**
- [ ] Numpress-linear m/z (default lossy; --no-numpress for lossless)
- [ ] Imaging extension (coordinate columns, metadata.imaging block) — out of scope for this port
- [ ] SDRF/ISA metadata embedding (--sdrf / --isa flags)
- [ ] Optical image embedding (--image)
- [ ] L2 conformance (L1 strict is default)
- [ ] Reporter-quant for labeled MS/MS (--reporter-quant)

---

## References

**File Paths (Rust Reference Implementation):**
- Spectrum write: `/refs/mzML2mzPeak/src/write/spectrum.rs` (lines 99–273)
- Writer integration: `/refs/mzML2mzPeak/src/write/writer.rs` (lines 159–200)
- Plain mzML path: `/refs/mzML2mzPeak/src/write/mzml.rs` (lines 184–445)
- Column schema: `/refs/mzML2mzPeak/src/schema/columns.rs` (IMS coordinates only; MS arrays derived from sample)
- CV governance: `/refs/mzML2mzPeak/src/schema/cv.rs` (lines 165–305)
- Metadata mapping: `/refs/mzML2mzPeak/src/schema/metadata.rs`
- Buffer descriptors: `/refs/mzPeak/src/buffer_descriptors.rs` (canonical schema definitions)

**Specifications:**
- mzPeak Specification: https://github.com/HUPO-PSI/mzPeak-specification
- mzData reader (Rust): https://github.com/mobiusklein/mzdata (v0.64.1, imzml feature)
- mzpeaks (Rust): https://crates.io/crates/mzpeaks (CentroidPeak, peak set types)
- Arrow/Parquet: https://arrow.apache.org/ (v57.0.0 canonical)

