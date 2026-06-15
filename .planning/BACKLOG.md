# Backlog

Deferred, non-blocking items. Promote into a milestone when prioritized.

| ID | Item | Notes |
|----|------|-------|
| BL-01 | Richer MGF validation / AnyCPU mzLib | The MGF tests' mzLib-based round-trip validation was replaced with a dependency-free `BEGIN IONS` spectrum count (dropped the x64-only mzLib so the suite runs native arm64, 52/52). If deeper MGF assertions are wanted later, either reintroduce mzLib **under an x64-only test guard**, or use an AnyCPU mzLib if upstream ever ships one (as of 1.0.579, `MassSpectrometry.dll` is still x64-pinned). **Not blocking** — current behavior is correct and fully native. |
| BL-02 | Profile compaction: zero-run stripping + null-marking + per-spectrum δmz model (ZRS-01..04) | Dropped from v2 Phase 4 by decision — the null-marking encode + WLS δmz-model fit + reconstruction is high-complexity/fidelity-risk for the size benefit. Chunk codec already DECODES null-marked reference files; ENCODE deferred. **Not blocking.** |
