# v2 Phase 4 Context: Profile Compaction (zero-run stripping + null-marking) + Ion Mobility

**Requirements:** ZRS-01..04, IM-01..02
**Milestone:** v2. **Depends on:** Phases 1-3 (chunk codec + numpress + streaming).

## Intent

Add **profile compaction** — zero-run stripping + null-marking with a per-spectrum δmz model — to shrink
profile spectra further and STRUCTURALLY match the reference (which null-marks 480/488 chunk rows). And
populate **ion-mobility** (FAIMS) columns (already present, currently null). Centroid `spectra_peaks` is
untouched. This is the last fidelity phase before the conformance/CLI closeout (Phase 5).

## Recon (verified against the reference)

- `refs/mzPeak/small.chunked.mzpeak`: **480/488 chunk rows have null entries in `mz_chunk_values`** → the
  reference null-marks aggressively.
- `spectrum.mz_delta_model` is a **variable-length list of doubles** per spectrum: e.g. `[β0,β1,β2]` (quadratic),
  `[β0]` (single), or `None`. So the model order adapts per spectrum (the WLS δmz ~ β0+β1·mz+β2·mz² fit, or
  fewer terms / none when too few points).
- `scan.ion_mobility_value`/`ion_mobility_type` are **all null** in the small reference (small.mzML has no
  FAIMS) → IM can be implemented but NOT validated against small.RAW (likely also no FAIMS). Emit values only
  when the Thermo file actually has FAIMS; else null (already the case).

## Decisions (research confirms the exact encode/model)

- **ZRS (null-marking), profile spectra_data only:** strip interior zero-intensity runs and null-mark flanking
  zeros (keep peak-defining points). The chunk codec's null-aware DECODE already exists (Phases 2-3); add the
  null-aware ENCODE: produce null entries in `mz_chunk_values`/intensity for stripped points, and fit + store
  the per-spectrum **`mz_delta_model`** (variable-order WLS coefficients) so a reader reconstructs the nulled
  m/z spacing. RESEARCH must nail: exactly which points are kept vs nulled, the WLS fit + order selection, and
  the reconstruction so peak apex/centroid is preserved (near-lossless).
- **Numpress interaction:** in numpress mode `mz_chunk_values` is already null (m/z in the bytes). RESEARCH must
  determine how null-marking composes with numpress (stripped points absent from the bytes? intensity nulls?
  δmz model still emitted?) by decoding `small.numpress.mzpeak` vs the unstripped signal.
- **Centroid untouched (ZRS-04):** `spectra_peaks` keeps all centroids, no stripping/marking.
- **Flag + default:** null-marking ON by default for profile in the lossy default mode (matches the reference,
  bigger win); a `--no-null-marking` (or similar) flag disables it; `--lossless` and `--point` imply NO
  null-marking (stay bitwise-L1). Confirm naming in plan.
- **Near-lossless (ZRS-03):** define + assert the reconstruction tolerance (peak shape/centroid preserved); this
  is an L2-class guarantee for the marked profile points (the kept non-zero points stay exact).
- **IM (FAIMS):** populate `scan.ion_mobility_value` (the FAIMS CV, double) + `ion_mobility_type`
  (CURIE MS:1001581 "FAIMS compensation voltage") from the Thermo scan trailer ("FAIMS CV"/"FAIMS Voltage On")
  reusing the existing TRFP ScanTrailer/extraction. selected_ion ion-mobility where applicable. Absent → null.

## Must NOT regress

- `--lossless` and `--point` remain BITWISE-L1 (no stripping). mzpeak-validate 0/0 in ALL modes.
- The kept non-zero profile points and all centroids stay exact; only stripped zero-intensity points are
  approximated via the δmz model on read-back.
- Streaming/memory + per-scan robustness preserved.

## Verification (this phase)

- Build + full suite green native arm64; new tests: null-mark encode→decode reconstruction within tolerance
  (peak apex/centroid preserved); δmz model fit correctness; centroid facet untouched; --lossless/--point
  still bitwise-L1; FAIMS columns populated for a FAIMS case (synthetic if no corpus FAIMS file) and null otherwise.
- mzpeak-validate 0/0 (numpress default w/ null-marking, --lossless, --point).
- Size: null-marked numpress < Phase-3 numpress on small.RAW; structurally closer to the reference (null ratio).
- (Phase-5 will re-run the corpus differential — null-marking should raise the match rate vs the reference.)

## Constraints / runtime

- Native arm64 (AnyCPU 8.0.37); DOTNET_ROLL_FORWARD; bin/Release; arch -arm64; mzpeak-validate; python3.11.
- Reuse the chunk codec (extend null-aware encode), MzPeakColumns/metadata builder (mz_delta_model column
  exists), ScanStager (FAIMS), MSNumpress. Compact code; explicit usings; BOM-free; NO process/phase comments.

## Reference artifacts
- `refs/mzPeak/small.chunked.mzpeak` (null-marked delta), `small.numpress.mzpeak` (null-marked numpress + mz_delta_model).
- `refs/_findings/mzpeak_groundtruth_schema.md`; the Rust reference null-marking + δmz model (RESEARCH to mine).
- `ThermoRawFileParser/Writer/MzPeak/MzPeakChunkCodec.cs`, `ScanStager.cs`, `MzPeakMetadataFacetBuilder.cs`.
