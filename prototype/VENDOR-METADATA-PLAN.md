# Vendor metadata: adversarial review + implementation plan

Status: `--vendor-metadata` is implemented prototype-grade on branch `raw-verbatim-metadata`
(commit 222b6d7): tall `vendor_scan_trailers` + `vendor_file_metadata` + `vendor_trailer_schema`,
0-error validate, 100/100 tests. This document hardens it to production.

## Adversarial review of the current implementation

| # | Severity | Finding | Fix (planned) |
|---|----------|---------|---------------|
| 1 | **HIGH** | **Index-space mismatch.** `vendor_scan_trailers.scan_index` is the raw Thermo **scan number**, but every other facet is keyed by the dense **ordinal**. They can't be joined without an external map, and the map isn't emitted. | Emit BOTH `scan_index` (verbatim raw scan number) and `ordinal` (emitted spectrum index) — capture the scanNumber→ordinal map (already built in `Write()`). |
| 2 | **HIGH** | **Second full pass + includes non-emitted scans.** `WriteVendorScanTrailers` re-reads trailers for every scan in [first,last] — a second ~744k-call pass on the Astral, and it captures scans that were filtered/skipped in the main loop (present in vendor table, absent from spectra). | Capture trailers **during** the main scan loop (the staging step already reads each trailer), keyed by ordinal → single pass, consistent set. |
| 3 | **MEDIUM** | **value_float via string parse is culture-ambiguous.** RawFileReader returns culture-formatted strings (trailers use `.`, RunHeaderEx props came back with `,`); `"1,234"` is ambiguous. | Use the **typed** `GetTrailerExtraValues(scan)` (objects typed per the header `DataType`) for `value_float`; keep `Values[]` strings for the verbatim `value`. Eliminates parsing guesswork. |
| 4 | **MEDIUM** | **Status-log timeseries dropped.** Only the run-start snapshot is captured in `file_metadata`. The per-RT status log (Astral: 200 labels × 428 timepoints — voltages/temps/pressures, prime QC data) is lost. | Add `vendor_status_log.parquet` (`position, rt, label, value, value_float`) tall. |
| 5 | LOW | **Error log dropped** (`RunHeaderEx.ErrorLogCount`>0 on Astral). | Optional `vendor_error_log.parquet` (`index, message`). |
| 6 | LOW | **`entity_type:"vendor"` is an invented extension.** The validator ignores it today, but a future validator could flag unknown facets. | Confirm the mzPeak spec's sanctioned mechanism for non-standard/auxiliary facets (RESEARCH); align or document the extension explicitly. |
| 7 | LOW | **Wide not offered.** Recommendation was tall-default + wide opt-in; only tall exists. | `--vendor-metadata[=tall\|wide\|both]`; wide builds typed columns from the trailer header (sanitized names; exact label kept in `vendor_trailer_schema`). |
| 8 | LOW | **Section-header noise.** Labels like `"=== Mass Calibration: ===:"` (empty value) are emitted as rows. Harmless, verbatim-faithful, but clutters. | Keep (verbatim) but flag them via a `is_section` boolean, or document. |
| 9 | LOW | **Test depth.** No typed-value assertion, no tall→wide pivot round-trip, no multi-GB smoke, no multi-segment-tune test. | Add those. |

No correctness defects in the emitted bytes themselves (verified: verbatim strings exact, validate 0 errors, pyarrow round-trip). The HIGH items are about **joinability and pass efficiency**, not data corruption.

## Implementation plan

### Phase A — Indexing & single-pass (correctness; HIGH items 1,2,3)
- Move trailer capture into the main scan loop: in the commit block, alongside `scanNumberToOrdinal[scanNumber]=ordinal`, stage `(ordinal, scanNumber, labels[], typedValues[], stringValues[])` into a streamed `VendorTrailerFacetStream` (temp file, 1M-row groups) **only for committed scans**.
- Schema → `vendor_scan_trailers(ordinal:u64, scan_number:i32, label:string, value:string, value_float:double?)`.
- Source `value` from `Values[i]` (verbatim), `value_float` from `GetTrailerExtraValues(scan)[i]` cast to double when numeric (no culture parse).
- Drop the second pass entirely.
- **DoD:** vendor rows ↔ spectra ordinals 1:1; no skipped scans leak in; one pass; small.RAW + Astral(file-level) verified.

### Phase B — Completeness (items 4,5)
- `vendor_status_log.parquet(position:i32, rt:double, label:string, value:string, value_float:double?)` over `GetStatusLogEntriesCount`/`GetStatusLogAtPosition` (or per-RT iteration). Stream.
- `vendor_error_log.parquet(index:i32, message:string)` (optional, tiny).
- Keep `vendor_file_metadata` (instrument/sample/run_header/tune/method); move the status-log *header* out (now its own timeseries table).
- **DoD:** status-log timeseries present + validates; documented.

### Phase C — Wide opt-in (item 7)
- Flag becomes `--vendor-metadata` (=tall default) and `--vendor-metadata=wide|both`.
- Wide: one row per ordinal; one typed column per trailer-header label (DataType→Parquet type); column name = sanitized label; exact label+unit+type preserved in `vendor_trailer_schema`.
- Handle MS1/MSn label-subset differences via the header union (null where absent).
- **DoD:** wide round-trips to the same values as the tall pivot; both modes validate.

### Phase D — Conformance & scale (item 6 + multi-GB)
- RESEARCH: mzPeak spec mechanism for auxiliary/vendor facets (file-level metadata blocks / `auxiliary_arrays` / userParam-like). Align `entity_type`/`data_kind` or document the extension in `mzpeak_index.json`.
- Confirm streaming memory profile on the 22 GB Astral (full `vendor_scan_trailers` ≈ 63 M rows); add a large-file read-back gate.
- **DoD:** 22 GB end-to-end with `--vendor-metadata`; validate 0 errors; pyarrow reads back the vendor facets.

### Phase E — Tests & docs (item 9)
- Typed `value_float` correctness; tall→wide pivot equivalence; multi-segment tune; status-log presence; large-file smoke; `--vendor-metadata=wide`.
- RUNNING.md already documents tall; extend for status-log + wide + the ordinal/scan_number keys.

## RESEARCH outcomes (deep-research, 103 agents, adversarially verified) — folded in

- **Tall is confirmed by prior art (HIGH).** "Best layout is split/tall plus a per-scan key/value table
  and a file-level block." **TRFP issue #116 favors the long (tall) format**; **AlphaRaw uses split
  tables**; rawrr/rawDiag read trailers by literal label. → keep tall default + the file-level block;
  our design matches the field's consensus.
- **The data is genuinely lost in RAW→mzML (HIGH).** msConvert/ProteoWizard **drops** sld sequence,
  instrument method, and status/error logs as "too verbose"; only a few trailer labels map to PSI-MS
  (injection time MS:1000927, FAIMS CV MS:1001581), and `<userParam>` is the *only* standards path
  (and can't scale to 63 M rows). → the vendor facets add real, otherwise-lost information.
- **Trailer labels are firmware-dependent (HIGH).** Reinforces tall (schema-stable) over wide and the
  `vendor_trailer_schema` sidecar; wide must tolerate per-firmware label drift.
- **Keying:** prior art reads by literal scan number/label. → Phase A's decision to emit **both**
  `ordinal` (archive-join) **and** `scan_number` (verbatim instrument identity) is the right reconcile.
- **UNVERIFIED by research:** the mzPeak spec's sanctioned home for vendor/auxiliary metadata, and the
  fuller IRawDataPlus surface (tune/status/run-header/error/auto-sampler). → I verified the latter
  empirically (the Astral probe). For the former, keep `entity_type:"vendor"` as a **documented
  extension** in `mzpeak_index.json` and check the HUPO-PSI/mzPeak spec directly in Phase D before
  finalizing (do not block on it).

Sources: ProteoWizard `RawFile.cpp` + issue #371, TRFP `MzMlSpectrumWriter.cs` + README + issue #116,
AlphaRaw, mzML 1.1 XSD (userParam/cvParam), PNNL Thermo-Raw-File-Reader. Net: **no plan change** — the
recommendation (tall + file-level block + schema sidecar) is corroborated; Phase A/B/C/D/E stand.
