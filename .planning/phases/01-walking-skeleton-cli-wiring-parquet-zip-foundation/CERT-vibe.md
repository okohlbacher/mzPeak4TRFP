- HIGH MzPeakSpectrumWriter.cs:169 - async-over-sync hazard from `.GetAwaiter().GetResult()` on WriteAsync - refactor to synchronous WriteFacet or propagate async through call stack
- HIGH MzPeakSpectrumWriter.cs:144-145 - spectrum metadata `id` and `time` fields marked nullable but ground-truth schema requires them - remove nullable=true and adjust def levels to `{1}`
- MEDIUM MzPeakSpectrumWriter.cs:150-152 - definition levels `{2}` for nullable fields incorrect when fields should be required - change def levels to `{1}` after fixing nullable
- MEDIUM MzPeakParquetTests.cs:103,159 - tests use `.GetAwaiter().GetResult()` pattern - same fix as first issue
- MEDIUM OutputFormat.cs:1,MainClass.cs:1,RawFileParser.cs:1,SpectrumWriter.cs:1 - files have UTF-8 BOM violating "Keep files BOM-free" constraint - strip BOM from all Phase 1 files
- LOW MzPeakSpectrumWriter.cs:21-27 - hardcoded SpectrumArrayIndex JSON string - extract to constant

VERDICT: CERTIFY-WITH-FIXES
