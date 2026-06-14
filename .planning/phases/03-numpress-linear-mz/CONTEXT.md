# v2 Phase 3 Context: Numpress-Linear m/z

**Requirements:** NP-01..04
**Milestone:** v2. **Depends on:** Phase 2 (chunked layout + codec; `chunk_encoding` branch point).

## Intent

Encode m/z with **Numpress-linear** into the chunk facet's `mz_numpress_linear_bytes` (the 7th chunk
field, present ONLY in Numpress mode), making **Numpress the new default** (smallest output; reference
`small.numpress.mzpeak` is ~63% smaller than delta-chunked). `--no-numpress`/`--lossless` falls back to
the Phase-2 delta chunks. Numpress m/z is **lossy** (bounded) → L2 conformance; intensity stays lossless f32.

## Decisions (research confirms against `refs/mzPeak/small.numpress.mzpeak`)

- **C# MSNumpress port** (linear encode + decode), vendored into the repo — pure managed, Apache-2.0,
  no x64/native deps. Reuse the canonical ms-numpress algorithm (encodeLinear / decodeLinear /
  optimalLinearFixedPoint). Decode is needed for the L2 round-trip tests.
- **Chunk schema in Numpress mode:** add `mz_numpress_linear_bytes: large_list<u8>` (7th field). Confirm
  from the reference EXACTLY how delta vs numpress modes differ: is `mz_chunk_values` null/absent when
  numpress bytes are present? what `chunk_encoding` CURIE marks numpress? Match the reference verbatim.
- **Transform recorded** in `spectrum_array_index` AND a `data_processing` method step (transform CURIE —
  RESEARCH to confirm the literal, likely the MS-Numpress-linear term e.g. MS:1002312; verify vs the file).
- **Default = numpress ON.** `--no-numpress` / `--lossless` → delta chunks (Phase-2 path, L1). A CLI warning
  + a `data_processing` note flag that default m/z is lossy.
- **L2 conformance:** decoded numpress m/z is within the numpress-linear bound of the source (define the
  bound from the fixed point used); intensity bit-exact f32. The bitwise-L1 gate from Phase 2 applies to
  `--lossless`/`--point` modes; numpress mode uses the bounded L2 check.
- intensity is NOT numpress'd in v2 (out of scope; stays lossless f32).

## Must NOT regress

- mzpeak-validate 0/0 in ALL modes (numpress default, `--lossless`, `--point`).
- `--lossless` and `--point` remain bitwise-L1 (Phase 1/2 behavior unchanged).
- Streaming/memory + per-scan robustness preserved (numpress encodes per-chunk in the streaming path).

## Verification (this phase)

- Build + full suite green native arm64; numpress encode/decode unit round-trip within bound (+ known
  MSNumpress test vectors if available); chunk schema diff vs `small.numpress.mzpeak`.
- mzpeak-validate 0/0 on numpress (default), `--lossless`, `--point` for small.RAW.
- L2: decode our numpress m/z vs source within bound; intensity exact. `--lossless` still bitwise-L1.
- Size: numpress default < delta-chunked < point for small.RAW (expect ~50-65% of chunked).

## Constraints / runtime

- Native arm64 (AnyCPU 8.0.37); DOTNET_ROLL_FORWARD; bin/Release; native dotnet/arch -arm64; mzpeak-validate; python3.11.
- Reuse the Phase-2 chunk codec + Phase-1 streaming Handle (branch on encoding inside the codec; do NOT fork
  the facet). Vendor numpress as a small self-contained source file. Compact code; explicit usings; BOM-free
  new files; NO comments referencing harness/process/phases. Record the numpress source license/provenance.

## Reference artifacts

- `refs/mzPeak/small.numpress.mzpeak` (authoritative numpress output), `refs/mzPeak/small.chunked.mzpeak` (delta).
- ms-numpress canonical algorithm (MSNumpress.cs / MSNumpress.cpp) — vendor the C# port.
- `.planning/phases/02-chunked-layout/02-01-SUMMARY.md` (chunk codec + encoding branch point).
- `ThermoRawFileParser/Writer/MzPeak/` (chunk codec), `Writer/MzPeakSpectrumWriter.cs`, `ParseInput.cs`, `MainClass.cs`.
