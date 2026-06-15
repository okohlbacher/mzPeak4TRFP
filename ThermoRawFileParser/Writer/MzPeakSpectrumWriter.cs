using log4net;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using ThermoFisher.CommonCore.Data.FilterEnums;
using ThermoFisher.CommonCore.Data.Interfaces;

namespace ThermoRawFileParser.Writer
{
    public partial class MzPeakSpectrumWriter : SpectrumWriter
    {
        private static readonly ILog Log =
            LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        // Row-count flush cap for the streamed data facets, a secondary guard against unbounded buffers
        // when individual rows are tiny. Point rows are ~20 bytes; fat chunk rows (lists of m/z values +
        // numpress bytes + intensities) are bounded primarily by RowGroupByteCap below.
        private const int RowGroupRowCap = 1_048_576;

        // Primary flush cap: approximate uncompressed bytes accumulated in the current row-group buffer.
        // Chunk rows carry variable-length lists, so a row-count cap alone produces a single multi-hundred-
        // megabyte row group on large inputs that standard readers cannot load. Flushing at a healthy
        // Parquet row-group size keeps every row group readable.
        private const long RowGroupByteCap = 64L * 1024 * 1024;

        // Optional override of the streamed row-count flush cap. When set, the streamed flush path uses
        // this value instead of RowGroupRowCap, lowering the per-row-group bound so a small input still
        // emits multiple row groups through the writer. Left null in normal operation.
        internal static int? TestRowGroupRowCap;

        // Optional override of the streamed byte flush cap, lowered by tests so a small input crosses the
        // byte budget and emits multiple row groups. Left null in normal operation.
        internal static long? TestRowGroupByteCap;

        private int Cap => TestRowGroupRowCap ?? RowGroupRowCap;
        private long ByteCap => TestRowGroupByteCap ?? RowGroupByteCap;

        private readonly CvCollector _cv = new CvCollector();
        private bool _chromFromDeviceTrace;
        private readonly List<MassAnalyzerType> _analyzerOrder = new List<MassAnalyzerType>();
        private readonly List<IonizationModeType> _ionizationOrder = new List<IonizationModeType>();
        private string _instrumentModel;
        private string _instrumentSerial;
        private DateTime _creationDate;
        private string _sourceName;
        private string _sourceLocation;

        public MzPeakSpectrumWriter(ParseInput parseInput) : base(parseInput)
        {
        }

        // Optional per-scan callback invoked AFTER the scan's filter key is staged but BEFORE the scan
        // commits. Throwing from it simulates a read/build failure on an already-keyed scan, exercising the
        // guarantee that a skipped parent commits no precursor-map entry and a later child never resolves
        // through it. Left null in normal operation.
        internal Action<int> AfterFilterKeyStaged;

        // Optional per-MSn callback invoked just before a child's precursor is built, with the child scan
        // number and the live scan->ordinal map. Removing the resolved parent's ordinal from the map here
        // simulates a parent that was read but never emitted, exercising the guarantee that the child does
        // not read a selected-ion intensity through that absent parent. Left null in normal operation.
        internal Action<int, IDictionary<int, ulong>> BeforeBuildPrecursor;

        // When set, CaptureTic skips the device TIC trace and uses the summed ScanStatistics TIC,
        // exercising the fallback path (TicSource == "summed"). Left false in normal operation.
        internal bool TestForceSummedTic;

        // Optional per-MSn override of the precursor charge state, keyed by child scan number. Returns
        // the charge to record (which may differ from the trailer's "Charge State:"), exercising the
        // non-null charge column on inputs whose trailer carries no charge. Left null in normal operation.
        internal Func<int, int?> TestChargeOverride;

        public override void Write(IRawDataPlus raw, int firstScanNumber, int lastScanNumber)
        {
            if (!raw.HasMsData)
            {
                throw new RawFileParserException("No MS data in RAW file, no output will be produced");
            }

            var instData = raw.GetInstrumentData();
            _instrumentModel = instData.Model;
            _instrumentSerial = instData.SerialNumber;
            _creationDate = raw.CreationDate;
            _sourceName = Path.GetFileName(ParseInput.RawFilePath);
            _sourceLocation = "file://" + (Path.GetDirectoryName(Path.GetFullPath(ParseInput.RawFilePath)) ?? "");

            var records = new List<MzPeakRecord>();
            var scanNumberToOrdinal = new Dictionary<int, ulong>();
            var dataSpectra = new HashSet<ulong>();
            var peakSpectra = new HashSet<ulong>();

            ulong ordinal = 0;

            // Constructed INSIDE the try so a temp-file/handle open that throws for a later facet still
            // disposes and deletes every facet created so far (the finally guards each handle for null).
            ISpectraDataFacet dataFacet = null;
            PointFacetStream peaksFacet = null;
            IChromDataFacet chromFacet = null;

            try
            {
                dataFacet = ParseInput.MzPeakPointLayout
                    ? (ISpectraDataFacet)new PointFacetStream(Cap, ByteCap)
                    : new ChunkFacetStream(Cap, ByteCap, ParseInput.MzPeakChunkSize, ParseInput.MzPeakNumpress);
                peaksFacet = new PointFacetStream(Cap, ByteCap);
                // Chromatograms follow the spectra layout: chunk over the time axis (numpress/delta) by
                // default, point layout (with ms_level) under --point.
                chromFacet = ParseInput.MzPeakPointLayout
                    ? (IChromDataFacet)new ChromDataFacetStream(Cap, ByteCap)
                    : new ChromChunkFacetStream(ParseInput.MzPeakNumpress);

                for (var scanNumber = firstScanNumber; scanNumber <= lastScanNumber; scanNumber++)
                {
                    StagedScan staged;
                    try
                    {
                        staged = StageScan(raw, scanNumber, ordinal, scanNumberToOrdinal);
                    }
                    catch (Exception ex)
                    {
                        Log.Error($"Scan #{scanNumber} cannot be processed because of the following exception: {ex.Message}");
                        Log.Debug($"{ex.StackTrace}\n{ex.InnerException}");
                        ParseInput.NewError();
                        continue;
                    }

                    // Out-of-range scan: skip silently, consuming no ordinal.
                    if (staged == null) continue;

                    // Commit block: scan fully succeeded. Apply ALL staged state atomically, including the
                    // shared precursor-resolution map — a scan that failed above never reaches here, so it
                    // cannot poison a later child's parent lookup. A write/flush/IO failure here is FATAL
                    // (bytes may already be on disk) and must propagate, never be swallowed as a skip.
                    // An empty in-range spectrum still emits its metadata row and consumes an ordinal but
                    // contributes no data/peaks rows.
                    if (staged.Mz.Length > 0)
                    {
                        dataFacet.Append(ordinal, staged.Mz, staged.Inten);
                        dataSpectra.Add(ordinal);
                    }
                    if (staged.PeakMz != null)
                    {
                        peaksFacet.Append(ordinal, staged.PeakMz, staged.PeakInten);
                        peakSpectra.Add(ordinal);
                    }
                    records.Add(staged.Rec);
                    scanNumberToOrdinal[scanNumber] = ordinal;
                    _precursorScanNumbers[staged.FilterKey] = scanNumber;
                    ordinal++;
                }

                if (ordinal == 0)
                {
                    throw new RawFileParserException("No in-range spectrum to write");
                }

                // Device TIC over the whole run (minutes, one value per scan in scan order), paired with
                // the per-scan (RT, ms_level) records. The device-trace value is the authoritative TIC;
                // the summed ScanStatistics.TIC differs for MS1 and is used only when the trace is
                // unavailable.
                var chromTime = new List<double>();
                var chromIntensity = new List<double>();
                var chromMsLevel = new List<long>();
                CaptureTic(raw, records, chromTime, chromIntensity, chromMsLevel);
                for (int i = 0; i < chromTime.Count; i++)
                    chromFacet.Append(chromTime[i], chromIntensity[i], chromMsLevel[i]);

                var hasPeaks = peaksFacet.PointCount > 0;

                // Chromatogram facets contribute their own CURIE prefixes; register them before the
                // metadata facet so the generated cv_list — finalized inside the metadata facet — covers
                // every prefix the archive uses.
                RegisterChromDataPrefixes();
                var numpress = !ParseInput.MzPeakPointLayout && ParseInput.MzPeakNumpress;
                if (!ParseInput.MzPeakPointLayout)
                {
                    CollectPrefix(numpress ? MzPeakCv.NumpressLinear : MzPeakCv.ChunkEncoding);
                    CollectPrefix(MzPeakCv.MzTransform);
                    CollectPrefix(MzPeakCv.IntensityTransform);
                }

                var dataMeta = ParseInput.MzPeakPointLayout
                    ? PointFooter(dataSpectra.Count, dataFacet.PointCount, false)
                    : ChunkFooter(dataSpectra.Count, dataFacet.PointCount, numpress);
                var peaksMeta = PointFooter(peakSpectra.Count, peaksFacet.PointCount, true);
                var chromMeta = new Dictionary<string, string>
                {
                    ["chromatogram_data_point_count"] = chromTime.Count.ToString(),
                    ["chromatogram_tic_source"] = _chromFromDeviceTrace ? "device" : "summed"
                };
                // The point layout self-describes its time/intensity/ms_level point buffers; the chunk
                // layout is read from its column names (the reference emits no chromatogram array_index).
                if (ParseInput.MzPeakPointLayout)
                    chromMeta["chromatogram_array_index"] = MzPeakLayout.ChromatogramArrayIndex;

                dataFacet.Close(dataMeta);
                peaksFacet.Close(peaksMeta);
                chromFacet.Close(chromMeta);

                var chromMetaBytes = BuildChromatogramMetadataFacet(records.Count, chromTime.Count);
                var metaBytes = BuildMetadataFacet(records);
                var indexBytes = BuildIndex(hasPeaks, true);

                ConfigureWriter(".mzpeak");
                try
                {
                    using (var zip = new ZipArchive(Writer.BaseStream, ZipArchiveMode.Create, true))
                    {
                        AddStored(zip, "mzpeak_index.json", indexBytes);
                        AddStoredFromFile(zip, "spectra_data.parquet", dataFacet.TempPath);
                        AddStored(zip, "spectra_metadata.parquet", metaBytes);
                        if (hasPeaks) AddStoredFromFile(zip, "spectra_peaks.parquet", peaksFacet.TempPath);
                        AddStored(zip, "chromatograms_metadata.parquet", chromMetaBytes);
                        AddStoredFromFile(zip, "chromatograms_data.parquet", chromFacet.TempPath);
                    }

                    Writer.Flush();
                }
                finally
                {
                    Writer.Close();
                }

                Log.Info($"Wrote mzPeak archive with {ordinal} spectra ({dataFacet.PointCount} data points, " +
                         $"{peaksFacet.PointCount} peak points, {chromFacet.PointCount} TIC points)");
            }
            finally
            {
                dataFacet?.Dispose();
                peaksFacet?.Dispose();
                chromFacet?.Dispose();
                TryDelete(dataFacet?.TempPath);
                TryDelete(peaksFacet?.TempPath);
                TryDelete(chromFacet?.TempPath);
            }
        }

        internal static void TryDelete(string path)
        {
            try { if (path != null && File.Exists(path)) File.Delete(path); }
            catch { }
        }

        // Footer KV for a streamed point facet. spectra_data and spectra_peaks carry distinct per-facet
        // array indices: spectra_data records the m/z + intensity transform CURIEs, spectra_peaks records
        // a null transform for the verbatim centroids.
        private static Dictionary<string, string> PointFooter(int spectrumCount, long pointCount, bool isPeaks) =>
            new Dictionary<string, string>
            {
                ["spectrum_count"] = spectrumCount.ToString(),
                ["spectrum_data_point_count"] = pointCount.ToString(),
                ["spectrum_array_index"] = isPeaks ? MzPeakLayout.PointPeaksArrayIndex : MzPeakLayout.PointDataArrayIndex
            };

        // Footer KV for the chunked spectra_data facet: same count keys as the point facet, with the chunk
        // spectrum_array_index instead of the point one.
        private static Dictionary<string, string> ChunkFooter(int spectrumCount, long pointCount, bool numpress) =>
            new Dictionary<string, string>
            {
                ["spectrum_count"] = spectrumCount.ToString(),
                ["spectrum_data_point_count"] = pointCount.ToString(),
                ["spectrum_array_index"] = numpress ? MzPeakLayout.NumpressSpectrumArrayIndex : MzPeakLayout.ChunkedSpectrumArrayIndex
            };

        private static void AddStored(ZipArchive zip, string name, byte[] bytes)
        {
            var entry = zip.CreateEntry(name, CompressionLevel.NoCompression);
            using (var s = entry.Open())
            {
                s.Write(bytes, 0, bytes.Length);
            }
        }

        // Streams a finished temp-file facet into a STORED zip entry via a 64 KB bounded CopyTo, never
        // reading the whole facet into a byte[].
        private static void AddStoredFromFile(ZipArchive zip, string name, string tempPath)
        {
            var entry = zip.CreateEntry(name, CompressionLevel.NoCompression);
            using (var es = entry.Open())
            using (var src = File.OpenRead(tempPath))
                src.CopyTo(es, 1 << 16);
        }

        private void RegisterChromDataPrefixes()
        {
            foreach (var curie in MzPeakLayout.ChromDataAccessions) CollectPrefix(curie);
        }

        private string Cv(string accession, string label, string unit = null) =>
            _cv.Cv(accession, label, unit);

        private void CollectPrefix(string curie) => _cv.Collect(curie);
    }
}
