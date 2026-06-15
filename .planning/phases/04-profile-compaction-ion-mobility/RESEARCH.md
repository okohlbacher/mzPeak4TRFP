# Phase 4: Profile Compaction (zero-run stripping + null-marking + δmz model) + Ion Mobility — Research

**Researched:** 2026-06-15
**Domain:** mzPeak chunk-codec null-aware ENCODE; per-spectrum WLS δmz model; numpress composition; Thermo FAIMS extraction
**Confidence:** HIGH (every numeric claim verified by decoding the reference artifacts with python3.11 + pyarrow + pynumpress + pyteomics, and by replicating the WLS fit to 8 significant figures against the reference `mz_delta_model`)

## Summary

The HUPO-PSI Rust reference performs profile compaction in three composable steps, all gated by a
single `--null-zeros` / `-u` CLI flag (`null_zeros: bool`): (1) **zero-run stripping** — drop
zero-intensity points that sit strictly between two zeros (`_skip_zero_runs_gen`); (2)
**null-marking** — for the surviving points, set BOTH m/z and intensity to null wherever a point is
part of a consecutive zero-intensity *pair* (`is_zero_pair_mask` + `nullif`); (3) **δmz model fit**
— a per-spectrum WLS regression of consecutive m/z spacing against m/z, stored in
`spectrum.mz_delta_model`, used by the reader to reconstruct the m/z of null-marked points
(`select_delta_model` + `fill_nulls_for`). [VERIFIED: refs/mzPeak/src/filter.rs, src/chunk_series.rs, src/writer/array_buffer.rs, examples/convert.rs]

I decoded `refs/mzPeak/small.chunked.mzpeak` and `small.numpress.mzpeak`. The "480/488" figure in the
recon is **chunk ROWS, not spectra**. The archive has **48 spectra: 14 profile + 34 centroid**. Only
the **14 profile MS1 spectra** are null-marked and carry a `mz_delta_model`; **all 34 centroid spectra
are untouched** (no null m/z, model is `None`). [VERIFIED: decoded spectra_metadata.parquet —
`mz_delta_model` length distribution `{None:34, 3:7, 1:7}`, representation `{MS:1000127 centroid:34,
MS:1000128 profile:14}`]

The δmz model alternates per spectrum: odd profile spectra (uniform 0.0909-spaced, FT-resampled) get a
**constant** model `[0.09090909]` → **bit-exact** reconstruction (measured 0.0 m/z error). Even profile
spectra (Orbitrap √-spaced) get a **quadratic** model `[β0,β1,β2]` → near-lossless reconstruction
(measured max 1.7e-5, mean 4.4e-8, p99 ~1e-6 m/z error vs the mzML source). [VERIFIED: full
reconstruction against `small.mzML` ground truth]

**Primary recommendation:** Add a null-aware ENCODE pre-pass to the chunk facet (strip zero-runs +
null-mark zero-pairs in BOTH m/z and intensity), fit the per-spectrum WLS δmz model and emit it into
`spectrum.mz_delta_model` (already an empty column), apply to **profile spectra only** in the lossy
default mode, gate behind ON-by-default with a `--mzpeak-no-null-marking` opt-out, and force it OFF in
`--lossless`/`--point` so those stay bitwise-L1. Populate FAIMS via the existing trailer path
(`"FAIMS Voltage On:"` + `"FAIMS CV:"` → `scan.ion_mobility_value` + `ion_mobility_type` =
`MS:1001581`).

## User Constraints (from CONTEXT.md)

### Locked Decisions
- **ZRS (null-marking), profile spectra_data only:** strip interior zero-intensity runs and null-mark
  flanking zeros (keep peak-defining points). The chunk codec's null-aware DECODE already exists; add
  the null-aware ENCODE: produce null entries in `mz_chunk_values`/intensity for stripped points, and
  fit + store the per-spectrum `mz_delta_model` (variable-order WLS coefficients).
- **Numpress interaction:** in numpress mode `mz_chunk_values` is already null (m/z in the bytes).
  Determine how null-marking composes with numpress.
- **Centroid untouched (ZRS-04):** `spectra_peaks` keeps all centroids, no stripping/marking.
- **Flag + default:** null-marking ON by default for profile in the lossy default mode; a
  `--no-null-marking` (or similar) flag disables it; `--lossless` and `--point` imply NO null-marking
  (stay bitwise-L1). Confirm naming in plan.
- **Near-lossless (ZRS-03):** define + assert the reconstruction tolerance; L2-class guarantee for the
  marked profile points; kept non-zero points stay exact.
- **IM (FAIMS):** populate `scan.ion_mobility_value` (FAIMS CV, double) + `ion_mobility_type`
  (CURIE MS:1001581 "FAIMS compensation voltage") from the Thermo scan trailer
  ("FAIMS CV"/"FAIMS Voltage On") reusing the existing TRFP ScanTrailer/extraction. selected_ion
  ion-mobility where applicable. Absent → null.

### Claude's Discretion
- Exact flag spelling (CONTEXT says "`--no-null-marking` (or similar)").
- Whether to implement the local-median-delta path for multi-point null runs (the reference does; see
  Open Question 1 — in practice small.mzML only exercises singleton-flank fills).

### Deferred Ideas (OUT OF SCOPE)
- Phase-5 corpus differential / match-rate uplift (this phase only needs the local size + structure win).
- timsTOF/Bruker ion-mobility unstack (3D mobility axis) — Thermo FAIMS is a per-scan scalar, not a 3D axis.

## Phase Requirements

| ID | Description | Research Support |
|----|-------------|------------------|
| ZRS-01 | Zero-run stripping for profile spectra_data | `_skip_zero_runs_gen` rule decoded + reproduced; kept count 13589 matches `number_of_data_points` exactly (Item 1) |
| ZRS-02 | Null-marking of flanking zero pairs + δmz model | `is_zero_pair_mask` rule decoded; WLS fit reproduced to 8 sig-figs (Items 1, 2) |
| ZRS-03 | Near-lossless reconstruction tolerance | Measured: constant model 0.0 error; quadratic model max 1.7e-5 m/z (Item 2) |
| ZRS-04 | Centroid `spectra_peaks` untouched | Verified: 0 null m/z in peaks facet; centroid spectra have `mz_delta_model = None` (Item 4) |
| IM-01 | Populate `scan.ion_mobility_value` / `ion_mobility_type` for FAIMS | Existing TRFP trailer path located (Item 5) |
| IM-02 | Absent FAIMS → null | Current code already leaves these leaf-null; preserve when no trailer key (Item 5) |

## Architectural Responsibility Map

| Capability | Primary Tier | Secondary Tier | Rationale |
|------------|-------------|----------------|-----------|
| Zero-run strip + null-mark m/z & intensity | Writer / chunk ENCODE (`ChunkFacetStream.Append` pre-pass) | — | Must run before `Chunk()`/`DeltaEncode`/numpress so chunk boundaries + lists carry the nulls |
| Per-spectrum δmz model fit | Writer / per-scan staging (`ScanStager` or a new fit step) | Metadata builder (emit `mz_delta_model`) | Fit needs the FULL (pre-strip) m/z + intensity; result is a metadata column |
| Reconstruction (null fill) | Reader / DECODE (`MzPeakChunkCodec.DeltaDecode` + a new model-fill) | — | Already partially present (null-aware decode); needs a `fill_nulls_for` analogue for round-trip tests |
| FAIMS CV extraction | Writer / `ScanStager.StageScan` (reuse `ScanTrailer`) | Metadata builder (emit ion_mobility cols) | Trailer is already read per scan in StageScan; value flows to `scan.*` |

## Standard Stack

No new external packages. All work is inside the existing TRFP C# writer and reuses already-present
dependencies. [VERIFIED: ParseInput.cs, MzPeakChunkCodec.cs, MzPeakMetadataFacetBuilder.cs,
ChunkFacetStream.cs, ScanTrailer.cs]

### Core (existing, reused)
| Component | Purpose | Why standard |
|-----------|---------|--------------|
| `MzPeakChunkCodec` | Delta encode/decode (null-aware), `Chunk()` window partition | Already the codec; extend ENCODE with null emission |
| `ChunkFacetStream` | Streams chunk rows (delta + numpress modes) | The single Append site where stripping/null-marking must inject |
| `ScanStager` / `MzPeakRecord` | Per-scan staging incl. `ScanTrailer` | Already constructs `ScanTrailer` (line 55) — FAIMS reuse point |
| `MzPeakMetadataFacetBuilder` | Emits `spectrum.mz_delta_model` (currently empty), `scan.ion_mobility_*` (currently null) | Columns exist; just populate |
| `MSNumpress` | numpress-linear m/z encode | Already used in numpress mode |

### Math primitives to implement (in C#, ~no deps)
| Primitive | Spec | Source |
|-----------|------|--------|
| WLS quadratic fit (`δ ~ β0+β1·mz+β2·mz²`) | QR solve of weighted design `[1, mz, mz²]`, weights `sqrt(ln(I+1))` | `filter.rs::fit_delta_model` + `RegressionDeltaModel::fit` [VERIFIED] |
| Constant δ model | median-below-median of deltas | `filter.rs::ConstantDeltaModel::fit` + `estimate_median_delta` [VERIFIED] |
| Model selection | pick constant iff `e_const < e_reg/10`, else regression | `filter.rs::select_delta_model` [VERIFIED] |
| zero-run strip | `_skip_zero_runs_gen` | `filter.rs` [VERIFIED] |
| zero-pair mask | `is_zero_pair_mask` | `filter.rs` [VERIFIED] |
| null fill (reader) | `fill_nulls_for` (singleton flanks via model.predict; multi-point runs via local median delta) | `filter.rs` [VERIFIED] |

**Installation:** none.

## Package Legitimacy Audit

No external packages are installed by this phase. Audit not applicable. (Research-time tooling
python3.11 / pyarrow 24.0.0 / pynumpress / pyteomics was used only to decode reference artifacts and
is not a project dependency.)

---

## Item 1 — Null-marking ENCODE algorithm (DECISION)

**Decision:** Apply, per profile spectrum, in this exact order BEFORE chunking:

1. **Zero-run strip** (`_skip_zero_runs_gen`): keep index `i` UNLESS `intensity[i]==0` AND it is in
   the interior of a zero run. Precise rule (drop = "strictly inside a run of zeros"):
   - A zero at `i` is **dropped** iff `(was_zero OR i==0/first-kept) AND ((i<n-1 AND inten[i+1]==0) OR i==n-1)`.
   - i.e. the FIRST zero after a peak is kept (trailing flank), the LAST zero before a peak is kept
     (leading flank), every zero strictly between them is dropped. A trailing run at the very end
     keeps exactly one zero.
2. **Null-mark zero pairs** (`is_zero_pair_mask` over the STRIPPED array): mark position `i` true iff
   `intensity[i]==0 AND (prev_was_zero OR (i<n-1 AND inten[i+1]==0))`. For every marked position set
   **BOTH** `mz := null` and `intensity := null`.

The net effect around an isolated peak `[0, p1..pk, 0]` is: interior zeros gone; the two zeros that
become adjacent across a peak gap (trailing-zero-of-A + leading-zero-of-B) form a **null PAIR**
(mz=null, int=null); the peak's own one flanking zero on the side facing real signal is kept as a real
0.0 only when it is NOT adjacent to another zero.

**Decoded worked example — `small.chunked.mzpeak` spectrum 0 (profile MS1):**
- mzML source: 19913 points, 8856 zeros, 11057 non-zero. [VERIFIED]
- After zero-run strip: **13589 kept** — exactly equals `spectrum.number_of_data_points = 13589`. [VERIFIED]
- Of those 13589, **2376 are null-marked** (mz & int set null) → 11213 remain real. [VERIFIED — matches
  the per-spectrum null count of 2376 read back from `mz_chunk_values`]
- Kept/null pattern around the first peak (post-strip indices), `keep` = real value, `NULL` = (null,null):

```
kept_idx 0:  mz=202.60657 int=0.000     keep   (leading flank zero of peak A)
kept_idx 1:  mz=202.60682 int=1938.117  keep
kept_idx 2:  mz=202.60707 int=2572.839  keep
kept_idx 3:  mz=202.60732 int=3392.107  keep
kept_idx 4:  mz=202.60757 int=3729.591  keep   (apex)
kept_idx 5:  mz=202.60782 int=2819.127  keep
kept_idx 6:  mz=202.60807 int=993.376   keep
kept_idx 7:  mz=202.60831 int=0.000     NULL   (trailing zero of A — part of pair)
kept_idx 8:  mz=204.75933 int=0.000     NULL   (leading zero of B — part of pair)
kept_idx 9:  mz=204.75959 int=1422.174  keep   (peak B rises)
kept_idx 10: mz=204.75984 int=3215.493  keep
```
[VERIFIED: reproduced `_skip_zero_runs_gen` + `is_zero_pair_mask` in python; pattern + counts match the reference byte-for-byte]

**C# encode (delta mode), per scan in `ChunkFacetStream.Append` (or a pre-pass before it):**
```
// inputs: mz[], intensity[] (sorted ascending, full profile)
kept = SkipZeroRuns(intensity)                 // indices to keep
mzK = mz[kept]; intK = intensity[kept]
mask = IsZeroPairMask(intK)                    // bool[] over kept
for i: if mask[i] { mzK[i] = null; intK[i] = null }   // BOTH null
// then existing path: Chunk(mzK-as-double-with-nulls), DeltaEncode (already null-aware), intensity list with nulls
```
The existing `MzPeakChunkCodec.DeltaEncode(double?[] mz, ...)` and the nullable `mz_chunk_values` /
`intensity` item lists in `ChunkFacetStream` ALREADY support null items — the ENCODE only needs the
pre-pass that introduces the nulls plus passing nullable intensity into `_intRows` (currently it
always sets `iHas[i]=true`; it must honor the mask). [VERIFIED: ChunkFacetStream lines 56–58, 134–168;
MzPeakChunkCodec.DeltaEncode lines 18–54]

**Chunk boundary nuance:** the reference `null_chunk_every_k` is null-aware — when a chunk boundary
lands on a null it skips paired nulls and avoids a length-1 chunk. The C# `MzPeakChunkCodec.Chunk`
currently takes `double[]` (no nulls) and rejects non-monotonic input. Plan must either (a) chunk on
the kept m/z WITH nulls (port the null-aware skip from `null_chunk_every_k`), or (b) chunk on the
non-null m/z positions and re-insert nulls into slices. Option (a) is the faithful port. The two
length-1-avoidance + final-residual rules already match between `Chunk` and `null_chunk_every_k`.
[CITED: refs/mzPeak/src/filter.rs::null_chunk_every_k lines 871–929]

---

## Item 2 — δmz model fit, variable order, reconstruction (DECISION)

**Decision — the exact fit math the C# must implement** (`select_delta_model`, run on the FULL
pre-strip arrays):

1. `deltas = [mz[i+1]-mz[i]]` (length n−1, aligned to `mz[1:]`). [VERIFIED]
2. **Weights** = `sqrt(ln(intensity + 1.0))` over the full intensity array, then sliced `[1:]` to align
   to `mz[1:]`. (Note: `build_delta_model` uses `(I+1).ln().sqrt()`; the chunk-codec unit test uses
   `sqrt(I)` — the WRITER path is `sqrt(ln(I+1))`, which is what produced the reference betas.) [VERIFIED:
   src/writer/base.rs:173 `ints.iter().map(|i| (*i + 1.0).ln().sqrt())`; reproduced betas to 8 sig-figs]
3. **Constant model:** `δ̂ = median-below-median(deltas)` — sort deltas, take median `m`, keep
   `deltas ≤ m`, take their median. `e_const = Σ (δ̂ − deltaᵢ)²`. [VERIFIED: estimate_median_delta]
4. **Regression model (rank=2, quadratic):** select rows where `deltaᵢ ≤ threshold` (threshold = 1.0),
   build design `X = [1, mzᵢ, mzᵢ²]` over `mz[1:]`, solve WLS via QR with Cholesky weights (weights are
   already √-transformed → multiply each design row and the target by `wᵢ`). Requires ≥3 rows after the
   `≤1.0` filter, else fall back to the constant model. `e_reg = Σ (predict(mzᵢ) − deltaᵢ)²` over ALL
   deltas. [VERIFIED: fit_delta_model + RegressionDeltaModel::fit]
5. **Order selection:** emit the **constant** model `[β0]` iff `e_const < e_reg / 10`; otherwise emit
   the **quadratic** `[β0,β1,β2]`. If the regression fit fails (singular / <3 pts) emit constant. If no
   m/z array, emit `None`. [VERIFIED: select_delta_model lines 187–216]

`predict(mz) = β0 + β1·mz + β2·mz²` (general: `Σ βᵢ·mzⁱ`). [VERIFIED: RegressionDeltaModel::predict]

**Reconstruction (reader) — `fill_nulls_for`:** the null markers come in three span shapes
(`NullStart` = leading null + run, `NullEnd` = run + trailing null, `NullBounded` = null + run + null).
For a **singleton-flanked** null (the common case here), the missing m/z is filled as
`real_value ± predict(real_value)` (subtract for a leading flank, add for a trailing flank). For a
**multi-point** real run inside a bounded span the flanks use a LOCAL median delta of that run instead
of the global model. [VERIFIED: filter.rs::fill_nulls_for lines 545–620]

**Measured reconstruction error (near-lossless bound, ZRS-03) — decoded + filled vs `small.mzML`:**

| Spectrum | Model | Model value | Max |Δm/z| | Mean |Δm/z| | p99 |Δm/z| |
|----------|-------|-------------|-----------|------------|-----------|
| 0 (Orbitrap √-spaced) | quadratic | `[-2.21e-08, 9.70e-11, 6.05e-09]` | **1.71e-05** | 4.42e-08 | ~1.08e-06 |
| 1 (FT uniform 0.0909) | constant | `[0.09090909]` | **0.0 (exact)** | 0.0 | 0.0 |
[VERIFIED: full decode+fill reconstruction]

**Reference per-spectrum models (all 14 profile spectra):** even indices (0,7,14,21,28,34,41) →
quadratic `[β0,β1,β2]`; odd indices (1,8,15,22,29,35,42) → constant `[0.09090909]`. 34 centroid
spectra → `None`. [VERIFIED]

**Replication check (spectrum 0):** my python WLS reproduced
`[-2.21139276e-08, 9.69754605e-11, 6.05422863e-09]` vs reference
`[-2.2113928e-08, 9.697546e-11, 6.0542286e-09]` — match to 8 significant figures, and `e_const`
(1.3001e4) is NOT < `e_reg/10` (1.298e3) so the regression model is correctly chosen. [VERIFIED]

**Tolerance to assert in tests (ZRS-03):** kept non-null points must be **bit-exact**;
null-filled points within **≤ 1e-4 m/z absolute** (generous over the measured 1.7e-5), and for the
constant-model (uniform-spacing) case assert **exact** equality. Apex/centroid is always a kept
non-null point → exact.

---

## Item 3 — Null-marking × numpress composition (DECISION)

**Decision:** In numpress mode the null-marked points are NOT explicitly null in the m/z stream;
instead **all kept points (including the flanking zeros) are encoded into `mz_numpress_linear_bytes`,
and the null markers are carried by ZERO INTENSITY** in `intensity_numpress_slof_bytes`. On read, the
decoder maps each `intensity == 0.0 → None`, sets the corresponding m/z to null, then re-fills m/z via
the `mz_delta_model`. `mz_chunk_values` stays **fully null** on every numpress row (already the case in
TRFP). The `mz_delta_model` IS still emitted.

**Decoded proof — `small.numpress.mzpeak` spectrum 0:**
- `mz_chunk_values[row]` = null (whole list null); `mz_numpress_linear_bytes[row]` present (838 B for
  chunk 0); `intensity_numpress_slof_bytes[row]` present. [VERIFIED]
- numpress-linear m/z chunk 0 decodes to **776 points**; total across spec0 = **13589** = same kept
  count as delta mode. So the bytes hold ONLY kept points (zero-runs already stripped) but DO include
  the flanking zeros. [VERIFIED]
- SLOF intensity chunk 0 decodes to 776 values, **142 of them == 0.0** at exactly the null-marked
  positions (e.g. idx 7 mz=202.60831 int=0.000, idx 8 mz=204.75933 int=0.000 — identical to the
  delta-mode null pair). [VERIFIED]

**Implication for the TRFP numpress path:** the SAME pre-pass (strip + null-mark) must run, but in
numpress mode the m/z null markers do NOT survive into the byte stream — they reappear as zero
intensity. Concretely: feed numpress the kept m/z (including flanking zeros, NO m/z nulls dropped), and
feed SLOF the kept intensity WITH the null-marked positions written as 0.0 (not dropped). The reader's
`v==0.0 → None` mapping (already in `chunk_series.rs::decode_arrow` numpress branch) plus the
`mz_delta_model` recovers the structure. Note TRFP currently uses **EncodeLinear for m/z + verbatim
intensity** in numpress mode (the reference uses SLOF for intensity); confirm Phase-3 intensity
encoding — if TRFP keeps intensity as a plain nullable list it can simply null those positions
directly; if it adopts SLOF it must write 0.0. [VERIFIED: ChunkFacetStream lines 108–131 — TRFP
numpress currently encodes m/z via `MSNumpress.EncodeLinear(win)` and writes intensity as a nullable
verbatim list; it does NOT yet do SLOF, so it can null the intensity list items directly, which is
simpler and equivalent on read.]

**Recommendation:** in TRFP numpress mode, drop zero-runs, feed ALL kept m/z (incl. flanks) to
EncodeLinear, and null the intensity list items at the zero-pair positions (TRFP's intensity is a
nullable list, not SLOF bytes). This matches the reference's read-back structure without needing SLOF.

---

## Item 4 — Which spectra get null-marked (DECISION)

**Decision:** **Profile spectra only** (`spectrum_representation == MS:1000128`), MS1 AND MSn alike (the
gate in the reference is `is_profile && nullify_zero_intensity`, not ms-level). Centroid spectra
(`MS:1000127`, routed to `spectra_peaks`) are **never** stripped/marked and get `mz_delta_model = None`.

**Verified counts in `small.chunked.mzpeak`:**
- 48 spectra total: 14 profile (all MS1 here) + 34 centroid. [VERIFIED]
- All 14 profile spectra have null m/z entries AND a non-null `mz_delta_model`. [VERIFIED]
- All 34 centroid spectra: 0 null m/z, `mz_delta_model = None`, and `spectra_peaks` has **0 null m/z
  rows**. [VERIFIED]
- `mz_delta_model` length distribution: `{None: 34, len-3: 7, len-1: 7}`. [VERIFIED]

In the reference small file there happen to be no profile MSn spectra, but the gate is representation,
not level — so a profile MS2 would also be null-marked. TRFP routes by
`mzData.isCentroided ? Centroid : Profile` (`ScanStager` line 80); use the SAME representation flag to
decide null-marking. [VERIFIED: ScanStager.cs:80]

---

## Item 5 — FAIMS / ion-mobility extraction (DECISION)

**Decision:** Reuse the EXISTING TRFP trailer path verbatim. The exact code already lives in
`MzMlSpectrumWriter` (lines 1284–1286) and `ParquetSpectrumWriter` (lines 162–164):

```csharp
double? FAIMSCV = null;
if (trailerData.AsBool("FAIMS Voltage On:").GetValueOrDefault(false))
    FAIMSCV = trailerData.AsDouble("FAIMS CV:");
```
[VERIFIED: MzMlSpectrumWriter.cs:1284-1286, ParquetSpectrumWriter.cs:162-164, ScanTrailer.cs]

`ScanStager.StageScan` ALREADY constructs `var trailer = new ScanTrailer(...)` at line 55, so the FAIMS
read costs one extra block there. Stage `FAIMSCV` onto `MzPeakRecord` (add fields, e.g.
`double? IonMobilityValue; string IonMobilityType;`).

**mzPeak columns (already in the schema, currently hard-null):**
- `scan.ion_mobility_value` (double) ← FAIMS CV value. [VERIFIED: MzPeakMetadataFacetBuilder.cs:80, BuildScanField:172]
- `scan.ion_mobility_type` (string CURIE) ← `MS:1001581` ("FAIMS compensation voltage"). [VERIFIED:
  the mzML path already emits accession `MS:1001581`, name "FAIMS compensation voltage" at
  MzMlSpectrumWriter.cs:1547-1550 — CONFIRMS the CURIE]
- When `FAIMS Voltage On:` is absent/off → leave BOTH leaf-null (current behavior at facet builder
  lines 80–82 is already all-null; just make it conditional). [VERIFIED]

**`selected_ion` ion-mobility:** the reference `selected_ion` struct has `ion_mobility_value` /
`ion_mobility_type` columns but the Thermo RAW path carries no per-selected-ion mobility — keep them
leaf-null on every MSn row (TRFP already does this at MzPeakMetadataFacetBuilder.cs:114-115). FAIMS CV
is a SCAN-level property in Thermo, not a selected-ion property. [VERIFIED]

**CV constant to add:** `MzPeakCv.FaimsCompensationVoltage = "MS:1001581"` (the facet builder needs a
string literal for `ion_mobility_type`). The `value` is a plain double in `ion_mobility_value` (no unit
CURIE column exists in the scan struct — the type column is the CURIE, the value column is the number).

**TESTING (small.RAW has no FAIMS):**
- `small.RAW` / `small2.RAW` at `ThermoRawFileParser/ThermoRawFileParserTest/Data/` almost certainly
  have no FAIMS — cannot validate populated values against them. [VERIFIED present]
- **Real FAIMS corpus file available:** `~/Claude/mzML2mzPeak/data/raw-examples/thermo-astral-MSV000100943/20240912_WFB_exp01_magnet_5_0.raw`
  is a Thermo Astral RAW (Astral acquisitions commonly use FAIMS). A Thermo Fusion Lumos RAW is also in
  the corpus (`thermo-fusion-lumos-PXD008952`). **Caveat:** the matching `local.mzML` files do NOT
  contain `MS:1001581` cvParams (the "1001581" grep hits were coincidental substrings inside scan-start
  -time values), so FAIMS presence in the RAW is UNVERIFIED in this session — confirm by reading the
  trailer of a scan from the astral RAW via TRFP before relying on it. [ASSUMED that the astral RAW
  carries FAIMS trailer keys — needs a one-off TRFP probe to confirm.]
- **Recommended test strategy (robust regardless of corpus):** a **synthetic / unit test** that feeds a
  fake `ScanTrailer` containing `"FAIMS Voltage On:" = "On"` and `"FAIMS CV:" = "-45.0"` and asserts
  `scan.ion_mobility_value == -45.0` and `scan.ion_mobility_type == "MS:1001581"`; plus a negative test
  with no FAIMS key asserting both stay null. `ScanTrailer` has a parameterless constructor and a
  dictionary back-end, so a trailer can be built directly in a test. [VERIFIED: ScanTrailer.cs:38-41]
  Optionally add an integration test against the astral RAW once FAIMS presence is probed.

---

## Item 6 — Default + flags (DECISION)

**Decision:**
- **Null-marking ON by default** in the lossy default mode (numpress chunk). This MATCHES the reference
  (the reference `small.numpress.mzpeak` and `small.chunked.mzpeak` were BOTH produced with `-u`). The
  reference's single `--null-zeros`/`-u` flag enables ALL of strip + null-mark + δmz fit together.
  [VERIFIED: refs/mzPeak/Justfile — `small.chunked` uses `-p -c -y -z -u`, `small.numpress` uses
  `--intensity-numpress-slof -c numpress:50 ... -y -z -u`; both carry `-u`]
- **Opt-out flag:** add a `--mzpeak-no-null-marking` (suggested spelling; CONTEXT allows "or similar")
  that sets `ParseInput.MzPeakNullMarking = false`. Default `true`.
- **`--lossless` and `--point` force null-marking OFF** → stay bitwise-L1. In TRFP these correspond to
  `MzPeakPointLayout = true` (point) and the lossless/no-numpress delta mode. The encode pre-pass must
  be skipped entirely when point layout OR lossless is selected, leaving the current bitwise path
  untouched. [VERIFIED: ParseInput.cs:111 `MzPeakPointLayout`, :115 `MzPeakNumpress`,
  MzPeakMetadataFacetBuilder.cs:118-121 mode selection]
- **Gate condition** for applying null-marking to a given spectrum:
  `MzPeakNullMarking && !MzPeakPointLayout && !lossless && isProfile`.

**Confirmation the other modes stay bitwise-L1:** point layout and lossless delta currently emit no
nulls and no model; with the gate above they remain unchanged → `mzpeak-validate 0/0` preserved in
`--lossless` and `--point`. The default numpress mode is already lossy on m/z (numpress), so adding
near-lossless null-marking does not change its conformance class (L2). [VERIFIED: existing modes have
no null/model emission today]

---

## Architecture Patterns

### Data flow (encode pre-pass injection)

```
StageScan (per scan)
  ├─ read mz[], intensity[]  (sorted ascending, full profile)
  ├─ trailer = ScanTrailer(...)          ── FAIMS: AsBool("FAIMS Voltage On:") + AsDouble("FAIMS CV:")
  │                                          → rec.IonMobilityValue / rec.IonMobilityType = MS:1001581
  └─ rec.Representation = isCentroided ? Centroid : Profile

write spectra_data (ChunkFacetStream.Append) — ONLY if profile && nullMarking && !point && !lossless:
  ├─ kept   = SkipZeroRuns(intensity)                 (drop interior zero-run points)
  ├─ mask   = IsZeroPairMask(intensity[kept])         (zero-pair → null markers)
  ├─ mzK/intK with mask positions set null            (BOTH m/z & intensity)
  ├─ model  = SelectDeltaModel(full mz, full intensity)  → rec.MzDeltaModel  (emit into metadata)
  ├─ delta mode:   Chunk(mzK incl nulls) → DeltaEncode (null-aware) ; intensity list with nulls
  └─ numpress mode: EncodeLinear(kept mz incl flank-zeros) ; intensity list with nulls at mask
                                                       (mz_chunk_values stays NULL — unchanged)

centroid spectra: spectra_peaks unchanged; rec.MzDeltaModel = None
```

### Anti-patterns to avoid
- **Do NOT fit the δmz model on the stripped array.** The fit uses the FULL pre-strip m/z + intensity
  (deltas of the original axis). Fitting post-strip changes the spacing distribution and the betas.
  [VERIFIED: build_delta_model is called on the sorted full `binary_array_map`, before stripping]
- **Do NOT null only the m/z (or only the intensity).** Both must be nulled at masked positions in
  delta mode; in numpress mode the m/z is kept in bytes and only intensity is zeroed/nulled.
- **Do NOT apply to centroid.** Would corrupt `spectra_peaks` and is explicitly out of scope (ZRS-04).
- **Do NOT drop the flanking zeros.** The strip keeps ONE flanking zero per peak side; only interior
  zeros are dropped. The kept flank zeros are what become the null pair.

## Don't Hand-Roll

| Problem | Don't build | Use instead | Why |
|---------|-------------|-------------|-----|
| Null-aware delta decode | new decoder | existing `MzPeakChunkCodec.DeltaDecode` | already null-aware + tested |
| numpress encode/decode | custom codec | existing `MSNumpress` | already wired in numpress mode |
| Trailer parsing / FAIMS bool+double | new parser | existing `ScanTrailer.AsBool/AsDouble` | exact keys already used by mzML path |
| Reader null-fill for round-trip test | ad-hoc | port `fill_nulls_for` | matches reference fill semantics exactly |

**Key insight:** the DECODE half already exists; this phase is almost entirely a small ENCODE pre-pass
plus a WLS fit plus wiring two already-present metadata columns.

## Runtime State Inventory

This is a code-only writer change (no rename/migration). No stored data, live-service config,
OS-registered state, secrets, or build artifacts carry phase-specific state.
- Stored data: **None** — verified, output archives are regenerated per run.
- Live service config: **None**.
- OS-registered state: **None**.
- Secrets/env vars: **None**.
- Build artifacts: standard `bin/Release` rebuild only; no stale-name artifacts.

## Common Pitfalls

### Pitfall 1: Weight formula mismatch
**What goes wrong:** using `sqrt(I)` (the chunk-codec unit test's weight) instead of `sqrt(ln(I+1))`
(the writer's actual weight) yields different betas that won't match the reference.
**How to avoid:** use `sqrt(ln(I+1))` exactly. **Warning sign:** betas differ from
`[-2.21e-08, 9.70e-11, 6.05e-09]` on small.mzML spectrum 0. [VERIFIED]

### Pitfall 2: Model fit on stripped data
**What goes wrong:** fitting after stripping changes the delta distribution → wrong model + worse
reconstruction. **How to avoid:** fit on the FULL sorted arrays before strip. (See Anti-patterns.)

### Pitfall 3: Chunk-boundary null handling
**What goes wrong:** the C# `Chunk()` takes `double[]` and rejects nulls/non-monotonic input; feeding it
the null-marked axis will throw. **How to avoid:** port `null_chunk_every_k`'s null-skip + length-1
avoidance, or chunk on non-null positions and re-insert nulls. [CITED: null_chunk_every_k]

### Pitfall 4: Order-selection threshold
**What goes wrong:** picking the wrong model order. The rule is `constant iff e_const < e_reg/10` (a 10×
margin favoring regression), NOT a simple `e_const < e_reg`. **Warning sign:** uniform-spacing spectra
should get `[β0]`, √-spaced should get `[β0,β1,β2]`; if reversed the threshold is wrong. [VERIFIED]

### Pitfall 5: number_of_data_points semantics
**What goes wrong:** emitting the original point count. The reference sets
`number_of_data_points = kept count after strip` (13589 for spec0, not 19913). **How to avoid:** set
`DataPointCount` to the kept count when null-marking is applied. [VERIFIED]

## Code Examples

### WLS quadratic δmz fit (reference-faithful, to port to C#)
```python
# Source: refs/mzPeak/src/filter.rs (fit_delta_model + RegressionDeltaModel::fit + select_delta_model)
deltas = diff(mz)                       # aligned to mz[1:]
w = sqrt(log(intensity + 1.0))[1:]      # writer weight (NOT sqrt(I))
sel = deltas <= 1.0                     # threshold filter
X = [[1, m, m*m] for m in mz[1:][sel]]
beta = WLS_QR_solve(X * w[sel], deltas[sel] * w[sel])   # rank=2 -> [b0,b1,b2]
# constant fallback:
const = median_below_median(deltas)
emit = [const] if sse(const) < sse(beta)/10 else beta   # else None if no mz
```
Reproduced `[-2.21139276e-08, 9.69754605e-11, 6.05422863e-09]` — matches reference to 8 sig-figs. [VERIFIED]

### Zero-run strip + zero-pair mask (reference-faithful)
```python
# Source: refs/mzPeak/src/filter.rs::_skip_zero_runs_gen + is_zero_pair_mask
def skip_zero_runs(inten):              # keep indices not strictly inside a zero run
    n=len(inten); n1=n-1; was_zero=False; acc=[]
    for i,v in enumerate(inten):
        if v==0:
            if (was_zero or not acc) and ((i<n1 and inten[i+1]==0) or i==n1): pass
            else: acc.append(i)
            was_zero=True
        else: acc.append(i); was_zero=False
    return acc
def zero_pair_mask(inten):              # over the KEPT array; True -> set mz&int null
    n=len(inten); n1=n-1; was_zero=False; acc=[]
    for i,v in enumerate(inten):
        if v==0:
            acc.append(was_zero or (i<n1 and inten[i+1]==0)); was_zero=True
        else: acc.append(False); was_zero=False
    return acc
```
[VERIFIED: reproduces the reference null pattern + counts exactly]

## State of the Art

| Old (current TRFP) | New (this phase) | Impact |
|--------------------|------------------|--------|
| Profile chunk emits every point, no nulls, empty `mz_delta_model` | strip + null-mark + per-spectrum WLS model | smaller numpress profile; structurally matches reference |
| `scan.ion_mobility_*` always null | FAIMS CV populated when trailer present | IM-01/02 satisfied |

**Deprecated/outdated:** none.

## Assumptions Log

| # | Claim | Section | Risk if wrong |
|---|-------|---------|---------------|
| A1 | The astral RAW (`20240912_WFB_exp01_magnet_5_0.raw`) actually contains FAIMS trailer keys | Item 5 | If absent, fall back to the synthetic ScanTrailer unit test (already recommended) — low risk |
| A2 | `--mzpeak-no-null-marking` is an acceptable flag spelling | Item 6 | Cosmetic; CONTEXT explicitly allows "or similar"; planner/discuss may rename |
| A3 | Threshold for the regression delta filter is `1.0` (literal `T::from(1.0)`) | Item 2 | Verified in source; only matters for spectra with genuine >1 m/z gaps (multi-peak) — confirmed present in spec0 fit |

## Open Questions

1. **Multi-point null-run fill (local median delta).** `fill_nulls_for` uses a LOCAL median delta for
   real runs >1 point inside a null-bounded span, vs the global model for singletons. small.mzML only
   exercises singleton flanks in my decode, so the multi-point branch is unverified end-to-end.
   - Known: the branch exists and is exercised by the reference's own unit tests.
   - Recommendation: port it for fidelity, but the round-trip TEST tolerance (≤1e-4) covers either path.

2. **TRFP numpress intensity representation.** The reference uses SLOF for intensity; TRFP currently
   writes intensity as a verbatim nullable list (not SLOF). In numpress mode, simply nulling the
   intensity list items at masked positions is equivalent on read (reader maps null→gap) and avoids
   SLOF — recommend this. Confirm Phase-3 didn't switch TRFP to SLOF.
   - Recommendation: keep TRFP's nullable-list intensity in numpress mode; null masked positions.

## Environment Availability

| Dependency | Required by | Available | Version | Fallback |
|------------|------------|-----------|---------|----------|
| .NET 8 / arm64 build | build + test | ✓ (per project) | 8.0.37 AnyCPU | — |
| python3.11 + pyarrow + pynumpress + pyteomics | research-only decode | ✓ | 3.11.3 / 24.0.0 / ok / installed | — |
| Real FAIMS Thermo RAW | FAIMS integration test | ✓ (astral corpus, presence unverified) | — | synthetic ScanTrailer unit test |
| `small.RAW` | regression (no-FAIMS → null) | ✓ | — | — |

**Missing with no fallback:** none. **Missing with fallback:** verified-FAIMS RAW (use synthetic
trailer test).

## Validation Architecture

> `.planning/config.json` not inspected for `nyquist_validation`; treating as enabled.

### Test framework
| Property | Value |
|----------|-------|
| Framework | NUnit (existing `ThermoRawFileParserTest`, e.g. `MzPeakWriterTests.cs`) |
| Quick run | `dotnet test --filter MzPeak` (arm64 Release) |
| Full suite | `dotnet test` + `mzpeak-validate` on emitted archives |

### Phase requirements → test map
| Req | Behavior | Test type | Command sketch | Exists? |
|-----|----------|-----------|----------------|---------|
| ZRS-01/02 | strip+null-mark encode → decode reconstruction within tol; apex exact | unit | new test in MzPeakWriterTests | ❌ Wave 0 |
| ZRS-02 | WLS δmz fit reproduces known betas on a synthetic spectrum | unit | new test | ❌ Wave 0 |
| ZRS-03 | near-lossless tolerance (≤1e-4; constant model exact) | unit | new test | ❌ Wave 0 |
| ZRS-04 | centroid `spectra_peaks` untouched; model None | unit | new test | ❌ Wave 0 |
| ZRS / regression | `--lossless` + `--point` stay bitwise-L1 | integration | `mzpeak-validate` 0/0 | extend existing |
| IM-01 | FAIMS trailer → ion_mobility_value + type MS:1001581 | unit (synthetic ScanTrailer) | new test | ❌ Wave 0 |
| IM-02 | no FAIMS → both null | unit | new test (small.RAW) | ❌ Wave 0 |

### Sampling rate
- Per task: `dotnet test --filter MzPeak`.
- Per wave: full `dotnet test` + `mzpeak-validate` on numpress-default / `--lossless` / `--point`.
- Phase gate: full suite green + `mzpeak-validate 0/0` in all three modes + size(null-marked numpress)
  < Phase-3 numpress on small.RAW.

### Wave 0 gaps
- [ ] Port reader `fill_nulls_for` (or a test-only reconstructor) for round-trip assertions.
- [ ] Synthetic ScanTrailer FAIMS fixture.
- [ ] Reference-beta golden values for the δmz fit test (use spectrum-0 betas above).

## Security Domain

Not applicable in the usual sense (no auth/session/network/crypto). The only input-validation surface
is numeric robustness: division/QR on degenerate spectra (≤3 valid deltas → fall back to constant
model; empty arrays → `None`), already handled by the reference's `<3 points` and `e_reg` guards. Port
those guards. No untrusted external input beyond the vendor RAW already parsed upstream.

## Sources

### Primary (HIGH)
- `refs/mzPeak/src/filter.rs` — `_skip_zero_runs_gen`, `is_zero_pair_mask`, `select_delta_model`,
  `fit_delta_model`, `RegressionDeltaModel`, `ConstantDeltaModel`, `fill_nulls_for`,
  `null_chunk_every_k`, `null_delta_encode/decode`.
- `refs/mzPeak/src/chunk_series.rs` — `ChunkingStrategy::encode_arrow/decode_arrow`, numpress branch,
  `from_arrays` (drop/nullify wiring), `decode_arrow` numpress `v==0.0 → None`.
- `refs/mzPeak/src/writer/array_buffer.rs` — `null_zeros` → `drop_zero_intensity` + `nullify_zero_intensity`;
  `apply_null_zero_transform_modifier` (NullInterpolate / NullZero).
- `refs/mzPeak/src/writer/base.rs` — `build_delta_model` (weights `sqrt(ln(I+1))`), gate `is_profile && nullify`.
- `refs/mzPeak/examples/convert.rs` + `Justfile` — `-u`/`--null-zeros` flag; both reference archives use `-u`.
- TRFP: `MzPeakChunkCodec.cs`, `ChunkFacetStream.cs`, `ScanStager.cs`, `MzPeakMetadataFacetBuilder.cs`,
  `ScanTrailer.cs`, `MzMlSpectrumWriter.cs` (FAIMS lines 1284-1286, 1542-1552), `ParseInput.cs`,
  `MzPeakRecord.cs`.
- Empirical decode (python3.11 + pyarrow 24.0.0 + pynumpress + pyteomics) of
  `small.chunked.mzpeak`, `small.numpress.mzpeak`, `small.mzpeak`, and `small.mzML`.

### Secondary (MEDIUM)
- `refs/_findings/mzpeak_groundtruth_schema.md`, `refs/_findings/mzpeak_mapping_report.md`.

### Tertiary (LOW)
- Astral corpus RAW FAIMS presence — assumed, needs a TRFP trailer probe (A1).

## Metadata

**Confidence breakdown:**
- Null-mark encode rule: HIGH — reproduced reference pattern + counts byte-for-byte.
- δmz fit + order selection: HIGH — betas matched to 8 sig-figs; model-length distribution matched.
- Numpress composition: HIGH — decoded both byte streams; kept-count + zero-marker positions matched.
- Centroid untouched: HIGH — 0 null m/z in peaks; model None for all 34 centroids.
- FAIMS path: HIGH (code path) / MEDIUM (corpus FAIMS file presence unverified).

**Research date:** 2026-06-15
**Valid until:** stable (reference artifacts are pinned in-repo; ~30 days nominal)
