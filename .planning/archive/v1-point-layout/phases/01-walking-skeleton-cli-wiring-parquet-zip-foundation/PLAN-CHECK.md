# Plan Check — Phase 1: Walking Skeleton (CLI Wiring + Parquet/ZIP Foundation)

**Checked:** 2026-06-14
**Plan:** 01-01-PLAN.md (single plan, 3 tasks, wave 1, depends_on: [])
**Verdict:** PASS WITH WARNINGS (no blockers; 3 warnings should be fixed before/during execution)

---

## Goal-Backward Result

Working backward from the 5 ROADMAP success criteria and the 6 requirement IDs
(CLI-01..03, PQ-01..03), every outcome is provably covered by a task whose acceptance
check is runnable and decisive. Codebase anchor points cited by the plan were verified
against live source; the OPEN-gate environment was verified live (python3.11 + all reader
deps importable; `MzPeakFile`/`spectrum_metadata` exist). No requirement is dropped, no
deferred/out-of-scope work leaked in, no scope reduction detected, dependencies are
correctly ordered (stub-before-real, helper-before-writer-use).

### Requirement Coverage (6/6)

| Req | Covered by | Acceptance is decisive? |
|-----|-----------|--------------------------|
| CLI-01 (`-f mzpeak`/`-f 4` selectable) | T1 enum before `None` (ordinal 4) + T3 end-to-end run | YES — `grep MzPeak` + `-f mzpeak` run produces `.mzpeak` |
| CLI-02 (help lists mzPeak, `.mzpeak` ext) | T1 help edit + ConfigureWriter `.mzpeak` branch | PARTIAL — see Warning W1 (help string not asserted by any command) |
| CLI-03 (dispatch builds MzPeakSpectrumWriter) | T1 dispatch case + T3 clean run | YES — `grep "new MzPeakSpectrumWriter"` + run exit 0 |
| PQ-01 (nested struct + list-of-struct + parallel nullable round-trip) | T2 NUnit round-trip | YES — `dotnet test --filter MzPeakParquet` asserts exact read-back values |
| PQ-02 (reusable PARAM builder + CV-column helper; ZSTD internal) | T2 BuildParamField/CvColumn + ZSTD read-back assert | YES — leaf order + ZSTD asserted in test |
| PQ-03 (STORED zip, ZSTD parquet, reader OPENs) | T3 STORED zip + both facets + Python OPEN gate | YES — `compress_type==0` + `MzPeakFile(...).spectrum_metadata len>=1` |

### Success Criteria → Provably TRUE after 3 tasks

1. `-f mzpeak` accepted/in help/`.mzpeak` output → T1+T3 (help text: see W1).
2. Dispatch builds writer; run completes 0 errors → T1+T3 (Errors==0 in `<done>`; see W2).
3. Parquet.Net nested/list/parallel round-trip → T2 (formalizes the already-passed spike).
4. Reusable PARAM + CV helpers exist → T2 (`MzPeakParquet.BuildParamField`, `CvColumn`).
5. STORED ZIP + ZSTD parquet + reader OPENs → T3 (structural + Python OPEN gate).

### Dependency / Ordering

- Single plan, internal task order T1 → T2 → T3 is correct:
  - T1 creates a **compiling stub** writer + all wiring (build-green checkpoint).
  - T2 builds the shared `MzPeakParquet` helper + round-trip proof **before** T3 consumes it.
  - T3 replaces the stub with real emission using the T2 helper.
- No circular/forward/missing plan dependencies (only one plan).

### Codebase anchors verified against live source

- `OutputFormat` enum `{MGF,MzML,IndexMzML,Parquet,None}` at OutputFormat.cs:3-10 → insert `MzPeak` before `None` (→ ordinal 4, `None`→5). Correct.
- Help text at MainClass.cs:530-531 reads "...3 for Parquet, 4 for None...". Plan edit accurate.
- Gzip guard at MainClass.cs:776 (`== OutputFormat.Parquet`). Plan extension accurate.
- ConfigureWriter Parquet binary branch at SpectrumWriter.cs:88. Plan extension accurate.
- Dispatch Parquet case at RawFileParser.cs:180-183; `using ThermoRawFileParser.Writer;` present. Accurate.
- `ReadMZData` is `private protected` (SpectrumWriter.cs:354) — accessible to a same-assembly subclass, so reuse is valid (plan calls it "inherited"; technically private-protected but works).
- `HasMsData`, `RetentionTimeFromScanNumber`, `NoPeakPicking` confirmed in ParquetSpectrumWriter analog.
- Fixture `Data/small.RAW` present. Test project (NUnit 4.2.2 + Parquet.Net 5.0.1) present. ImplicitUsings disabled (matches plan constraint).
- OPEN gate live-verified: python3.11 present; pyarrow/pandas/numpy/pynumpress/psims import; `MzPeakFile` (reader.py:965) + `spectrum_metadata` attr exist.

### Context compliance (CONTEXT.md)

All locked decisions honored: low-level Parquet API (not POCO), STORED zip + ZSTD,
new `MzPeakSpectrumWriter` mirroring ParquetSpectrumWriter, reusable PARAM/CV helpers in a
support file, m/z f64 / intensity f32, one-real-spectrum honesty. No deferred/out-of-scope
(v2 OPT-*, imaging, reverse conversion) work present. No scope reduction language found.

### Skipped dimensions

- Dimension 8 (Nyquist): SKIPPED — `config.json workflow.nyquist_validation: false`. VALIDATION.md not required.
- Dimension 10 (CLAUDE.md): SKIPPED — no `./CLAUDE.md` in repo.
- Dimension 7c (Architectural tiers): map present in RESEARCH; tasks place CLI/dispatch in MainClass/RawFileParser, encoding in writer+helper, extraction in base — consistent. PASS.

---

## Warnings (should fix; not blocking)

### W1 — CLI-02 help-text edit has no automated assertion
Task 1's `<automated>` greps `OutputFormat.cs` and `RawFileParser.cs` but never asserts the
**help string** contains "mzPeak", even though CLI-02 explicitly requires it and RESEARCH's
test map lists it as a unit check ("assert help string contains 'mzPeak'"). The edit could be
silently skipped and the gate would still go green.
**Fix:** add to Task 1 verify, e.g. `&& grep -q "4 for mzPeak" ThermoRawFileParser/MainClass.cs`.

### W2 — `Errors==0 && Warnings==0` is in `<done>` prose but not asserted by Task 3's command
Success criterion 2 and the PATTERNS "Error/warning accounting" pattern require the clean
run to keep Errors/Warnings at 0. Task 3's automated command checks STORED+index+OPEN but
does **not** assert the error/warning counters; a run that logs per-scan errors via
`NewError()` but still emits an openable archive would pass. The NUnit integration test that
would assert `parseInput.Errors == 0` (TestParquetCentroid analog) is deferred to Phase 4 per
PATTERNS, leaving Phase 1 without a machine check on this criterion.
**Fix:** either add an NUnit `OutputFormat.MzPeak` integration test in Task 3 asserting
`Errors==0/Warnings==0` (cheap — analog exists), or have the run command fail on nonzero
errors (e.g. assert TRFP exit code / scan log).

### W3 — Runtime roll-forward documented in prose but absent from acceptance commands
The machine has only .NET 9/10 (verified: SDK 10.0.301, no net8 runtime). `<build_runtime_setup>`
correctly prescribes `DOTNET_ROLL_FORWARD=LatestMajor` / `DOTNET_ROLL_FORWARD_TO_PRERELEASE=1`,
but none of the three `<automated>` blocks export them, so each `dotnet build/test/run` will
fail out-of-the-box unless the executor remembers the prose. This makes every acceptance check
non-self-contained.
**Fix:** prefix each `<automated>` with the env exports (or have Task 1 install the .NET 8
runtime once and document which path was taken in SUMMARY).

---

## Notes (non-blocking, no action required to pass)

- N1 — RESEARCH.md `## Open Questions` is NOT suffixed `(RESOLVED)`; both listed questions are
  explicitly deferred to Phase 4 / Phase 2 with rationale and are out of Phase 1 scope, so they
  do not gate this phase. Cosmetic: mark the section resolved-for-phase-1 to satisfy a strict
  research-resolution check.
- N2 — Plan files_modified lists `ThermoRawFileParser/Writer/MzPeak/MzPeakParquet.cs`; RESEARCH/
  PATTERNS suggest `Writer/MzPeak/MzPeakSchema.cs`. Filename divergence is harmless (planner's
  discretion); both land in the `Writer/MzPeak/` support location the decisions require.
- N3 — Task 2 test file path: frontmatter/`<files>` use the correct on-disk path
  `ThermoRawFileParser/ThermoRawFileParserTest/MzPeakParquetTests.cs` (verified the test csproj
  lives there). Consistent.

---

## Recommendation

PASS. The 3 tasks, executed in order, make all 5 success criteria TRUE and cover all 6
requirement IDs with decisive, live-verified acceptance gates. Address W1–W3 (all small,
mechanical additions to the existing verify blocks) to make every criterion machine-checked
and the acceptance commands self-contained; none of them block execution.
