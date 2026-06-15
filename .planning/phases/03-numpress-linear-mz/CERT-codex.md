HIGH, `ThermoRawFileParser/ThermoRawFileParserTest/MzPeakChunkTests.cs:979`: L2 gate only asserts global `worstAbs <= 5e-7`, not each value against its own chunk’s `0.5/fp` bound. A bad high-fp chunk could exceed its real bound and still pass. Fix by comparing each decoded value to the same-position lossless m/z under that row’s `bound`.

MEDIUM, `ThermoRawFileParser/MainClass.cs:804`: Numpress default emits `Log.Warn` but does not call `parseInput.NewWarn()`, so `--warningsAreErrors` still exits 0 and the final count says `0 warnings`. Fix by counting it or downgrade it from warning semantics.

LOW, `ThermoRawFileParser/Writer/MzPeakSpectrumWriter.cs:64`: Shipped comments retain review/process wording (`verified against the reference`, `Option B`). Fix by replacing with neutral domain comments.

VERDICT: CERTIFY-WITH-FIXES
