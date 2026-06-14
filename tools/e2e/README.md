# End-to-end corpus comparison

Converts every **Thermo** RAW in the `mzML2mzPeak` corpus that has a reference `.mzpeak`,
then validates and compares our output against that reference.

## Pieces

- `corpus_pairs.json` — the matched list: each Thermo RAW (detected by Finnigan magic bytes, not
  by extension/dir name) paired with its reference `.mzpeak` under `~/Claude/mzML2mzPeak/data/`.
  Rebuild by re-running the discovery (see project notes). 97 pairs.
- `compare_mzpeak.py OURS REF [--json]` — semantic comparison of two archives. Aligns spectra by
  nativeID (falls back to index), unions each spectrum's signal across `spectra_data` + `spectra_peaks`
  (decodes point, delta-chunk, and numpress-linear-chunk layouts), and reports per-spectrum
  (m/z, intensity) multiset agreement + ms_level/polarity/RT. `verdict: PASS` when the spectrum sets
  match and every reference spectrum's signal is reproduced in ours.
- `run_corpus_e2e.py` — orchestrates convert → `mzpeak-validate` → compare over all pairs,
  smallest-first, bounded concurrency, per-file timeout, temp cleanup, resumable. Writes
  `out/results.json` (resume state) and `out/report.md`.

## Run

```bash
# full corpus (resumable; safe to re-run / interrupt)
python3.11 tools/e2e/run_corpus_e2e.py --workers 2 --timeout 1800

# quick subset (smallest K)        # skip the multi-GB files
python3.11 tools/e2e/run_corpus_e2e.py --limit 10
python3.11 tools/e2e/run_corpus_e2e.py --max-gb 1.5
```

Requires the x64 Release build (`bin/x64/Release/net8.0/ThermoRawFileParser.dll`), the Rosetta
x64 .NET runtime at `~/.dotnet-x64` (see `RUNNING.md`), `mzpeak-validate` on PATH, and
`python3.11` with pyarrow + pynumpress.

## Notes

- The corpus references are produced by `mzML2mzPeak` from centroided mzML, so their signal lives in
  `spectra_peaks` (chunk `spectra_data` empty); TRFP routes centroid-acquired scans to `spectra_data`.
  The comparator unions both facets, so the routing difference is not a false mismatch.
- v1 accumulates each facet in memory; multi-GB RAW files may exceed memory/timeout — those are
  recorded as `TIMEOUT`/`CONVERT_FAIL` rather than aborting the run (a documented v1 limitation).
