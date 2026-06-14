# Backlog

Deferred, non-blocking items. Promote into a milestone when prioritized.

| ID | Item | Notes |
|----|------|-------|
| BL-01 | Richer MGF validation / AnyCPU mzLib | The MGF tests' mzLib-based round-trip validation was replaced with a dependency-free `BEGIN IONS` spectrum count (dropped the x64-only mzLib so the suite runs native arm64, 52/52). If deeper MGF assertions are wanted later, either reintroduce mzLib **under an x64-only test guard**, or use an AnyCPU mzLib if upstream ever ships one (as of 1.0.579, `MassSpectrometry.dll` is still x64-pinned). **Not blocking** — current behavior is correct and fully native. |
