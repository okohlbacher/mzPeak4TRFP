# Backlog

Deferred, non-blocking items. Promote into a milestone when prioritized.

| ID | Item | Notes |
|----|------|-------|
| BL-01 | Richer MGF validation / AnyCPU mzLib | The MGF tests' mzLib-based round-trip validation was replaced with a dependency-free `BEGIN IONS` spectrum count (dropped the x64-only mzLib so the suite runs native arm64, 52/52). If deeper MGF assertions are wanted later, either reintroduce mzLib **under an x64-only test guard**, or use an AnyCPU mzLib if upstream ever ships one (as of 1.0.579, `MassSpectrometry.dll` is still x64-pinned). **Not blocking** — current behavior is correct and fully native. |
| BL-02 | Profile compaction: zero-run stripping + null-marking + per-spectrum δmz model (ZRS-01..04) | ⊘ **Moot under the v3 mzPeak.NET-delegation pivot** — the data encoding is now owned by the vendored library, not TRFP, so this bespoke-writer feature no longer applies. Kept for record. |
| BL-03 | Byte-aware row-group flush in mzPeak.NET | mzPeak.NET's `BufferedSize` undercounts list/chunk payloads, so its byte cap (`RowGroupSize`) doesn't bound row groups; TRFP works around it with `EntryBufferSize=500` (spectrum-count proxy). A true byte-aware estimate in the library would be more precise and is a good upstream PR. **Not blocking** — all files validate 0/0 with the current proxy. |
| BL-04 | `--point` differential regression test | ✓ **Effectively covered** — `MzPeakDifferentialTests` already runs `-f 4 --point` end-to-end. Only net-new would be a `mzpeak-validate` assert on `number_of_data_points`, but those tests `Assert.Ignore` without python/validator on PATH → low value. Closed unless a CI validator gate is added. |
