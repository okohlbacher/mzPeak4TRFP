using log4net;
using Newtonsoft.Json.Linq;
using Parquet.Data;
using Parquet.Schema;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using ThermoFisher.CommonCore.Data.Business;
using ThermoFisher.CommonCore.Data.FilterEnums;
using ThermoFisher.CommonCore.Data.Interfaces;
using ThermoRawFileParser.Util;
using ThermoRawFileParser.Writer.MzML;

namespace ThermoRawFileParser.Writer
{
    public class MzPeakSpectrumWriter : SpectrumWriter
    {
        private static readonly ILog Log =
            LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private const string MzPeakVersion = "0.9";
        private const string MsCvVersion = "4.1.254";
        private const string UoCvVersion = "2026-01-16";

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

        // The nine chrom-data CURIEs whose prefixes must reach cv_list (finalized inside the metadata
        // facet). Registered via RegisterChromDataPrefixes before BuildMetadataFacet regardless of
        // whether chrom-data is streamed or buffered. Defined once in MzPeakCv.
        internal static readonly string[] ChromDataAccessions = MzPeakCv.ChromDataAccessions;

        // Point spectra_data: the m/z and intensity point arrays carry their per-facet transform CURIEs
        // (MS:1003901 m/z, MS:1003902 intensity) with data_processing_id and sorting_rank, matching the
        // reference point spectra_data footer.
        private const string PointDataArrayIndex =
            "{\"prefix\":\"point\",\"entries\":[" +
            "{\"context\":\"spectrum\",\"path\":\"point.mz\",\"data_type\":\"MS:1000523\",\"array_type\":\"MS:1000514\"," +
            "\"array_name\":\"m/z array\",\"unit\":\"MS:1000040\",\"buffer_format\":\"point\",\"transform\":\"MS:1003901\"," +
            "\"data_processing_id\":null,\"buffer_priority\":\"primary\",\"sorting_rank\":0}," +
            "{\"context\":\"spectrum\",\"path\":\"point.intensity\",\"data_type\":\"MS:1000521\",\"array_type\":\"MS:1000515\"," +
            "\"array_name\":\"intensity array\",\"unit\":\"MS:1000131\",\"buffer_format\":\"point\",\"transform\":\"MS:1003902\"," +
            "\"data_processing_id\":null,\"buffer_priority\":\"primary\",\"sorting_rank\":null}" +
            "]}";

        // Point spectra_peaks: centroided peaks are stored verbatim, so both arrays carry a null
        // transform, matching the reference point spectra_peaks footer.
        private const string PointPeaksArrayIndex =
            "{\"prefix\":\"point\",\"entries\":[" +
            "{\"context\":\"spectrum\",\"path\":\"point.mz\",\"data_type\":\"MS:1000523\",\"array_type\":\"MS:1000514\"," +
            "\"array_name\":\"m/z array\",\"unit\":\"MS:1000040\",\"buffer_format\":\"point\",\"transform\":null," +
            "\"data_processing_id\":null,\"buffer_priority\":\"primary\",\"sorting_rank\":0}," +
            "{\"context\":\"spectrum\",\"path\":\"point.intensity\",\"data_type\":\"MS:1000521\",\"array_type\":\"MS:1000515\"," +
            "\"array_name\":\"intensity array\",\"unit\":\"MS:1000131\",\"buffer_format\":\"point\",\"transform\":null," +
            "\"data_processing_id\":null,\"buffer_priority\":\"primary\",\"sorting_rank\":null}" +
            "]}";

        private const string ChunkedSpectrumArrayIndex =
            "{\"prefix\":\"chunk\",\"entries\":[" +
            "{\"context\":\"spectrum\",\"path\":\"chunk.mz_chunk_start\",\"data_type\":\"MS:1000523\",\"array_type\":\"MS:1000514\"," +
            "\"array_name\":\"m/z array\",\"unit\":\"MS:1000040\",\"buffer_format\":\"chunk_start\",\"transform\":\"MS:1003901\"," +
            "\"data_processing_id\":null,\"buffer_priority\":\"primary\",\"sorting_rank\":0}," +
            "{\"context\":\"spectrum\",\"path\":\"chunk.mz_chunk_end\",\"data_type\":\"MS:1000523\",\"array_type\":\"MS:1000514\"," +
            "\"array_name\":\"m/z array\",\"unit\":\"MS:1000040\",\"buffer_format\":\"chunk_end\",\"transform\":\"MS:1003901\"," +
            "\"data_processing_id\":null,\"buffer_priority\":\"primary\",\"sorting_rank\":0}," +
            "{\"context\":\"spectrum\",\"path\":\"chunk.mz_chunk_values\",\"data_type\":\"MS:1000523\",\"array_type\":\"MS:1000514\"," +
            "\"array_name\":\"m/z array\",\"unit\":\"MS:1000040\",\"buffer_format\":\"chunk_values\",\"transform\":\"MS:1003901\"," +
            "\"data_processing_id\":null,\"buffer_priority\":\"primary\",\"sorting_rank\":0}," +
            "{\"context\":\"spectrum\",\"path\":\"chunk.chunk_encoding\",\"data_type\":\"MS:1000523\",\"array_type\":\"MS:1000514\"," +
            "\"array_name\":\"m/z array\",\"unit\":\"MS:1000040\",\"buffer_format\":\"chunk_encoding\",\"transform\":\"MS:1003901\"," +
            "\"data_processing_id\":null,\"buffer_priority\":\"primary\",\"sorting_rank\":0}," +
            "{\"context\":\"spectrum\",\"path\":\"chunk.intensity\",\"data_type\":\"MS:1000521\",\"array_type\":\"MS:1000515\"," +
            "\"array_name\":\"intensity array\",\"unit\":\"MS:1000131\",\"buffer_format\":\"chunk_secondary\",\"transform\":\"MS:1003902\"," +
            "\"data_processing_id\":null,\"buffer_priority\":\"primary\",\"sorting_rank\":null}" +
            "]}";

        // m/z-only numpress layout: the four m/z anchor entries (MS:1003901) and the plain intensity
        // entry (chunk_secondary, MS:1003902) are unchanged; the m/z values live in
        // mz_numpress_linear_bytes (chunk_transform, MS:1002312, sorting_rank 0). 6 entries.
        private const string NumpressSpectrumArrayIndex =
            "{\"prefix\":\"chunk\",\"entries\":[" +
            "{\"context\":\"spectrum\",\"path\":\"chunk.mz_chunk_start\",\"data_type\":\"MS:1000523\",\"array_type\":\"MS:1000514\"," +
            "\"array_name\":\"m/z array\",\"unit\":\"MS:1000040\",\"buffer_format\":\"chunk_start\",\"transform\":\"MS:1003901\"," +
            "\"data_processing_id\":null,\"buffer_priority\":\"primary\",\"sorting_rank\":0}," +
            "{\"context\":\"spectrum\",\"path\":\"chunk.mz_chunk_end\",\"data_type\":\"MS:1000523\",\"array_type\":\"MS:1000514\"," +
            "\"array_name\":\"m/z array\",\"unit\":\"MS:1000040\",\"buffer_format\":\"chunk_end\",\"transform\":\"MS:1003901\"," +
            "\"data_processing_id\":null,\"buffer_priority\":\"primary\",\"sorting_rank\":0}," +
            "{\"context\":\"spectrum\",\"path\":\"chunk.mz_chunk_values\",\"data_type\":\"MS:1000523\",\"array_type\":\"MS:1000514\"," +
            "\"array_name\":\"m/z array\",\"unit\":\"MS:1000040\",\"buffer_format\":\"chunk_values\",\"transform\":\"MS:1003901\"," +
            "\"data_processing_id\":null,\"buffer_priority\":\"primary\",\"sorting_rank\":0}," +
            "{\"context\":\"spectrum\",\"path\":\"chunk.chunk_encoding\",\"data_type\":\"MS:1000523\",\"array_type\":\"MS:1000514\"," +
            "\"array_name\":\"m/z array\",\"unit\":\"MS:1000040\",\"buffer_format\":\"chunk_encoding\",\"transform\":\"MS:1003901\"," +
            "\"data_processing_id\":null,\"buffer_priority\":\"primary\",\"sorting_rank\":0}," +
            "{\"context\":\"spectrum\",\"path\":\"chunk.intensity\",\"data_type\":\"MS:1000521\",\"array_type\":\"MS:1000515\"," +
            "\"array_name\":\"intensity array\",\"unit\":\"MS:1000131\",\"buffer_format\":\"chunk_secondary\",\"transform\":\"MS:1003902\"," +
            "\"data_processing_id\":null,\"buffer_priority\":\"primary\",\"sorting_rank\":null}," +
            "{\"context\":\"spectrum\",\"path\":\"chunk.mz_numpress_linear_bytes\",\"data_type\":\"MS:1000523\",\"array_type\":\"MS:1000514\"," +
            "\"array_name\":\"m/z array\",\"unit\":\"MS:1000040\",\"buffer_format\":\"chunk_transform\",\"transform\":\"MS:1002312\"," +
            "\"data_processing_id\":null,\"buffer_priority\":\"primary\",\"sorting_rank\":0}" +
            "]}";

        private const string ChromatogramArrayIndex =
            "{\"prefix\":\"point\",\"entries\":[" +
            "{\"context\":\"chromatogram\",\"path\":\"point.time\",\"data_type\":\"MS:1000523\",\"array_type\":\"MS:1000595\"," +
            "\"array_name\":\"time array\",\"unit\":\"UO:0000031\",\"buffer_format\":\"point\",\"buffer_priority\":\"primary\",\"sorting_rank\":0}," +
            "{\"context\":\"chromatogram\",\"path\":\"point.intensity\",\"data_type\":\"MS:1000521\",\"array_type\":\"MS:1000515\"," +
            "\"array_name\":\"intensity array\",\"unit\":\"MS:1000131\",\"buffer_format\":\"point\",\"buffer_priority\":\"primary\"}," +
            "{\"context\":\"chromatogram\",\"path\":\"point.ms_level\",\"data_type\":\"MS:1000522\",\"array_type\":\"MS:1000786\"," +
            "\"array_name\":\"ms level\",\"unit\":\"UO:0000186\",\"buffer_format\":\"point\",\"buffer_priority\":\"primary\"}" +
            "]}";

        // One PARAM value, used to collect CV prefixes for the generated cv_list.
        private sealed class Param
        {
            public string Accession;
            public string Name;
            public string Unit;
            public double? Float;
            public long? Integer;
            public string String;
            public bool? Boolean;
        }

        private sealed class Record
        {
            public ulong Ordinal;
            public int ScanNumber;
            public int MsLevel;
            public string Id;
            public double Time;
            public sbyte? Polarity;
            public string Representation;
            public string SpectrumType;
            public double? LowestMz;
            public double? HighestMz;
            public double? BasePeakMz;
            public float? BasePeakIntensity;
            public float TotalIonCurrent;
            public ulong DataPointCount;
            public ulong? PeakCount;
            public List<Param> SpectrumParams = new List<Param>();

            public float ScanStartTime;
            public uint? PresetScanConfiguration;
            public string FilterString;
            public float? IonInjectionTime;
            public uint InstrumentConfigRef;
            public float WindowLower;
            public float WindowUpper;

            public bool IsMsn;
            public bool HasReaction;
            public ulong? PrecursorIndex;
            public string PrecursorId;
            public float? IsolationTarget;
            public float? IsolationLowerOffset;
            public float? IsolationUpperOffset;
            public List<Param> ActivationParams = new List<Param>();
            public double? SelectedIonMz;
            public int? ChargeState;
            public float? SelectedIonIntensity;
        }

        private readonly HashSet<string> _cvPrefixes = new HashSet<string>();
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

        // Everything staged from a single scan, held in locals until the scan fully succeeds. A read/build
        // failure discards the whole struct: no ordinal, no rows, no precursor-map entry are committed.
        private sealed class StagedScan
        {
            public double[] Mz, PeakMz;
            public float[] Inten, PeakInten;
            public Record Rec;
            public string FilterKey;
        }

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

            var records = new List<Record>();
            var scanNumberToOrdinal = new Dictionary<int, ulong>();
            var dataSpectra = new HashSet<ulong>();
            var peakSpectra = new HashSet<ulong>();

            ulong ordinal = 0;

            // Constructed INSIDE the try so a temp-file/handle open that throws for a later facet still
            // disposes and deletes every facet created so far (the finally guards each handle for null).
            ISpectraDataFacet dataFacet = null;
            PointFacetStream peaksFacet = null;
            ChromDataFacetStream chromFacet = null;

            try
            {
                dataFacet = ParseInput.MzPeakPointLayout
                    ? (ISpectraDataFacet)new PointFacetStream(Cap, ByteCap)
                    : new ChunkFacetStream(Cap, ByteCap, ParseInput.MzPeakChunkSize, ParseInput.MzPeakNumpress);
                peaksFacet = new PointFacetStream(Cap, ByteCap);
                chromFacet = new ChromDataFacetStream(Cap, ByteCap);

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
                var chromIntensity = new List<float>();
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
                    ["chromatogram_data_point_count"] = "0",
                    ["chromatogram_array_index"] = ChromatogramArrayIndex,
                    ["chromatogram_tic_source"] = _chromFromDeviceTrace ? "device" : "summed"
                };

                dataFacet.Close(dataMeta);
                peaksFacet.Close(peaksMeta);
                chromFacet.Close(chromMeta);

                var chromMetaBytes = BuildChromatogramMetadataFacet(records.Count);
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

        // Reads and builds everything for one scan into per-scan locals, touching NO shared/streaming
        // state. Returns null for an out-of-range or empty scan (a clean skip); returns the staged scan on
        // success. Any read/build failure throws and is handled by the caller as an error-counted skip, so
        // a failed scan never commits an ordinal, rows, or a precursor-map entry.
        private StagedScan StageScan(IRawDataPlus raw, int scanNumber, ulong ordinal,
            IDictionary<int, ulong> scanNumberToOrdinal)
        {
            var scanFilter = raw.GetFilterForScanNumber(scanNumber);
            int level = (int)scanFilter.MSOrder;
            if (level > ParseInput.MaxLevel || !ParseInput.MsLevel.Contains(level)) return null;

            var scanEvent = raw.GetScanEventForScanNumber(scanNumber);
            var mzData = ReadMZData(raw, scanEvent, scanNumber, false, false, false);

            var (mz, inten) = OrderedPairs(mzData.masses, mzData.intensities);

            double[] peakMz = null;
            float[] peakInten = null;
            ulong? peakCount = null;
            if (scanEvent.ScanData == ScanDataType.Profile && Scan.FromFile(raw, scanNumber).HasCentroidStream)
            {
                var peakData = ReadMZData(raw, scanEvent, scanNumber, true, false, false);
                var (pMz, pInten) = OrderedPairs(peakData.masses, peakData.intensities);
                if (pMz.Length > 0)
                {
                    peakMz = pMz;
                    peakInten = pInten;
                    peakCount = (ulong)pMz.Length;
                }
            }

            var scanStats = raw.GetScanStatsForScanNumber(scanNumber);
            var trailer = new ScanTrailer(raw.GetTrailerExtraInformation(scanNumber));

            // Mirror the mzML parent-derivation bookkeeping: map each scan's filter prefix to its scan
            // number so an MSn child can resolve its parent via the scan string.
            var filterMatch = level == 1
                ? null
                : (level == 2
                    ? _filterStringIsolationMzPattern.Match(scanEvent.ToString())
                    : _filterStringParentMzPattern.Match(scanEvent.ToString()));
            var filterKey = level == 1 ? "" : (filterMatch != null && filterMatch.Success ? filterMatch.Groups[1].Value : "");

            // The filter key is staged but nothing has committed yet. A failure raised here is
            // indistinguishable from a real post-key read failure and must leave no trace of this scan.
            AfterFilterKeyStaged?.Invoke(scanNumber);

            var rec = new Record
            {
                Ordinal = ordinal,
                ScanNumber = scanNumber,
                MsLevel = level,
                Id = ConstructSpectrumTitle((int)Device.MS, 1, scanNumber),
                Time = raw.RetentionTimeFromScanNumber(scanNumber),
                Polarity = scanFilter.Polarity == PolarityType.Negative
                    ? (sbyte?)-1
                    : (scanFilter.Polarity == PolarityType.Positive ? (sbyte?)1 : null),
                Representation = mzData.isCentroided ? MzPeakCv.CentroidSpectrum : MzPeakCv.ProfileSpectrum,
                SpectrumType = level == 1 ? MzPeakCv.Ms1Spectrum : MzPeakCv.MsnSpectrum,
                LowestMz = mz.Length > 0 ? (double?)mz[0] : null,
                HighestMz = mz.Length > 0 ? (double?)mz[mz.Length - 1] : null,
                BasePeakMz = mzData.basePeakMass,
                BasePeakIntensity = mzData.basePeakIntensity.HasValue ? (float?)mzData.basePeakIntensity.Value : null,
                TotalIonCurrent = (float)scanStats.TIC,
                DataPointCount = (ulong)mz.Length,
                PeakCount = peakCount,
                ScanStartTime = (float)raw.RetentionTimeFromScanNumber(scanNumber),
                PresetScanConfiguration = null,
                FilterString = scanEvent.ToString(),
                IonInjectionTime = trailer.AsDouble("Ion Injection Time (ms):").HasValue
                    ? (float?)(float)trailer.AsDouble("Ion Injection Time (ms):").Value : null,
                InstrumentConfigRef = AnalyzerIndex(scanFilter.MassAnalyzer, scanFilter.IonizationMode),
                WindowLower = (float)scanStats.LowMass,
                WindowUpper = (float)scanStats.HighMass
            };

            if (level >= 2)
            {
                BeforeBuildPrecursor?.Invoke(scanNumber, scanNumberToOrdinal);
                BuildPrecursor(raw, scanNumber, scanEvent, scanFilter, trailer, scanNumberToOrdinal,
                    filterKey, rec);
            }

            return new StagedScan
            {
                Mz = mz,
                Inten = inten,
                PeakMz = peakMz,
                PeakInten = peakInten,
                Rec = rec,
                FilterKey = filterKey
            };
        }

        private static void TryDelete(string path)
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
                ["spectrum_array_index"] = isPeaks ? PointPeaksArrayIndex : PointDataArrayIndex
            };

        // Footer KV for the chunked spectra_data facet: same count keys as the point facet, with the chunk
        // spectrum_array_index instead of the point one.
        private static Dictionary<string, string> ChunkFooter(int spectrumCount, long pointCount, bool numpress) =>
            new Dictionary<string, string>
            {
                ["spectrum_count"] = spectrumCount.ToString(),
                ["spectrum_data_point_count"] = pointCount.ToString(),
                ["spectrum_array_index"] = numpress ? NumpressSpectrumArrayIndex : ChunkedSpectrumArrayIndex
            };

        // Returns the (mz,intensity) pairs in non-decreasing m/z order with the full multiset
        // preserved. The Thermo SegmentedScan/CentroidStream are already ascending; a paired
        // index sort is applied only when an ascending violation is detected, never dropping or
        // merging equal-m/z points.
        public static (double[] mz, float[] intensity) OrderedPairs(double[] masses, double[] intensities)
        {
            var n = masses?.Length ?? 0;
            var mz = new double[n];
            var inten = new float[n];
            bool ascending = true;
            for (int i = 0; i < n; i++)
            {
                mz[i] = masses[i];
                inten[i] = (float)intensities[i];
                if (i > 0 && masses[i] < masses[i - 1]) ascending = false;
            }

            if (ascending) return (mz, inten);

            var order = new int[n];
            for (int i = 0; i < n; i++) order[i] = i;
            Array.Sort(order, (a, b) =>
            {
                int c = masses[a].CompareTo(masses[b]);
                return c != 0 ? c : a.CompareTo(b);
            });
            var sortedMz = new double[n];
            var sortedInten = new float[n];
            for (int i = 0; i < n; i++)
            {
                sortedMz[i] = masses[order[i]];
                sortedInten[i] = (float)intensities[order[i]];
            }
            return (sortedMz, sortedInten);
        }

        private void CaptureTic(IRawDataPlus raw, List<Record> records, List<double> time,
            List<float> intensity, List<long> msLevel)
        {
            ChromatogramSignal[] trace;
            try
            {
                raw.SelectInstrument(Device.MS, 1);
                var settings = new ChromatogramTraceSettings(TraceType.TIC);
                var data = raw.GetChromatogramData(new IChromatogramSettings[] { settings }, -1, -1);
                trace = ChromatogramSignal.FromChromatogramData(data);
            }
            catch (Exception ex)
            {
                Log.Warn($"Device TIC trace unavailable, falling back to summed TIC: {ex.Message}");
                trace = Array.Empty<ChromatogramSignal>();
            }

            if (trace.Length == 0)
            {
                foreach (var r in records)
                {
                    time.Add(r.Time);
                    intensity.Add(r.TotalIonCurrent);
                    msLevel.Add(r.MsLevel);
                }
                _chromFromDeviceTrace = false;
                return;
            }

            var signal = trace[0];
            if (signal.Times.Count == records.Count)
            {
                for (int i = 0; i < records.Count; i++)
                {
                    time.Add(signal.Times[i]);
                    intensity.Add((float)signal.Intensities[i]);
                    msLevel.Add(records[i].MsLevel);
                }
                _chromFromDeviceTrace = true;
                return;
            }

            var byScan = new Dictionary<int, (double time, double intensity)>();
            for (int k = 0; k < signal.Scans.Count; k++)
            {
                byScan[signal.Scans[k]] = (signal.Times[k], signal.Intensities[k]);
            }

            bool allDevice = true;
            foreach (var r in records)
            {
                if (byScan.TryGetValue(r.ScanNumber, out var sample))
                {
                    time.Add(sample.time);
                    intensity.Add((float)sample.intensity);
                }
                else
                {
                    time.Add(r.Time);
                    intensity.Add(r.TotalIonCurrent);
                    allDevice = false;
                }
                msLevel.Add(r.MsLevel);
            }
            _chromFromDeviceTrace = allDevice;
        }

        private byte[] BuildChromatogramMetadataFacet(int n)
        {
            var chromatogram = BuildChromatogramField();
            var precursor = BuildPrecursorField();
            var selectedIon = BuildSelectedIonField();
            var schema = new ParquetSchema(chromatogram, precursor, selectedIon);

            var cols = new Dictionary<DataField, (Array, int[], int[])>();
            var present = new[] { true };

            AddScalar(cols, schema, "chromatogram/index", new ulong[] { 0UL }, present);
            AddScalar(cols, schema, "chromatogram/id", new[] { "TIC" }, present);
            AddScalar(cols, schema, "chromatogram/" + Cv(MzPeakCv.ScanPolarity, "scan_polarity"),
                new sbyte[] { 0 }, present);
            CollectPrefix(MzPeakCv.MzArrayData);
            AddScalar(cols, schema, "chromatogram/" + Cv(MzPeakCv.ChromatogramType, "chromatogram_type"),
                new[] { MzPeakCv.MzArrayData }, present);

            var dprLeaf = Leaf(schema, "chromatogram/data_processing_ref");
            var dprRows = new[] { MzPeakParquet.AtLevel(dprLeaf.MaxDefinitionLevel - 1, false) };
            var (dprDef, _) = MzPeakParquet.NestedLevels(dprLeaf, dprRows);
            cols[dprLeaf] = (new string[0], dprDef, null);

            AddScalar(cols, schema, "chromatogram/" + Cv(MzPeakCv.NumberOfDataPoints, "number_of_data_points"),
                new ulong[] { (ulong)n }, present);

            AddEmptyList(cols, schema, "chromatogram/parameters");
            AddEmptyList(cols, schema, "chromatogram/auxiliary_arrays");
            AddScalar(cols, schema, "chromatogram/number_of_auxiliary_arrays", new uint[] { 0u }, present);

            AddNullPrecursor(cols, schema);
            AddNullSelectedIon(cols, schema);

            var custom = new Dictionary<string, string>
            {
                ["chromatogram_count"] = "1",
                ["chromatogram_data_point_count"] = "0"
            };

            return WriteFacet(schema, custom, cols);
        }

        private StructField BuildChromatogramField()
        {
            return new StructField("chromatogram",
                new DataField<ulong>("index", true),
                new DataField<string>("id", true),
                new DataField<sbyte>(Cv(MzPeakCv.ScanPolarity, "scan_polarity"), true),
                new DataField<string>(Cv(MzPeakCv.ChromatogramType, "chromatogram_type"), true),
                new DataField<string>("data_processing_ref", true),
                new DataField<ulong>(Cv(MzPeakCv.NumberOfDataPoints, "number_of_data_points"), true),
                new ListField("parameters", MzPeakParquet.BuildParamField("item")),
                new ListField("auxiliary_arrays", BuildAuxArrayField("item")),
                new DataField<uint>("number_of_auxiliary_arrays", true));
        }

        private StructField BuildAuxArrayField(string name)
        {
            return new StructField(name,
                new ListField("data", new DataField<byte>("item")),
                MzPeakParquet.BuildParamField("name"),
                new DataField<string>("data_type", true),
                new DataField<string>("compression", true),
                new DataField<string>("unit", true),
                new ListField("parameters", MzPeakParquet.BuildParamField("item")),
                new DataField<string>("data_processing_ref", true));
        }

        // A present-but-empty list on a single-row facet (the list value is defined, no elements).
        // Every descendant leaf gets the empty-list level, including auxiliary_arrays whose element
        // struct carries a non-nullable data leaf (data/list/item: uint8 not null).
        private void AddEmptyList(IDictionary<DataField, (Array, int[], int[])> cols, ParquetSchema schema,
            string listPath)
        {
            var list = (ListField)FindField(schema, listPath);
            foreach (var leaf in schema.GetDataFields())
            {
                if (!leaf.Path.ToString().StartsWith(listPath + "/")) continue;
                var rows = new[] { MzPeakParquet.EmptyList(list) };
                var (def, rep) = MzPeakParquet.NestedLevels(leaf, rows);
                cols[leaf] = (EmptyArray(leaf.ClrType), def, rep);
            }
        }

        private void AddNullPrecursor(IDictionary<DataField, (Array, int[], int[])> cols, ParquetSchema schema)
        {
            foreach (var leaf in schema.GetDataFields())
            {
                if (!leaf.Path.ToString().StartsWith("precursor/")) continue;
                AddNullLeaf(cols, leaf);
            }
        }

        private void AddNullSelectedIon(IDictionary<DataField, (Array, int[], int[])> cols, ParquetSchema schema)
        {
            foreach (var leaf in schema.GetDataFields())
            {
                if (!leaf.Path.ToString().StartsWith("selected_ion/")) continue;
                AddNullLeaf(cols, leaf);
            }
        }

        // A single all-null row for a leaf in a present-but-null top-level struct: definition level
        // sits one below the leaf's own present level (the owning struct is null), no value slot.
        private static void AddNullLeaf(IDictionary<DataField, (Array, int[], int[])> cols, DataField leaf)
        {
            var rows = new[] { MzPeakParquet.AtLevel(0, false) };
            var (def, rep) = MzPeakParquet.NestedLevels(leaf, rows);
            cols[leaf] = (EmptyArray(leaf.ClrType), def, rep);
        }

        private static Array EmptyArray(Type clr) => Array.CreateInstance(clr, 0);

        private uint AnalyzerIndex(MassAnalyzerType analyzer, IonizationModeType ionization)
        {
            int idx = _analyzerOrder.IndexOf(analyzer);
            if (idx < 0)
            {
                _analyzerOrder.Add(analyzer);
                _ionizationOrder.Add(ionization);
                idx = _analyzerOrder.Count - 1;
            }
            return (uint)idx;
        }

        private void BuildPrecursor(IRawDataPlus raw, int scanNumber, IScanEvent scanEvent,
            IScanFilter scanFilter, ScanTrailer trailer, IDictionary<int, ulong> scanNumberToOrdinal,
            string filterKey, Record rec)
        {
            rec.IsMsn = true;

            // Parent scan via Master Scan Number trailer, falling back to the scan-string parent the
            // mzML path derives. Map the parent scan number to its emitted ordinal; if the parent was
            // filtered out / not emitted, keep the precursor entry but leave precursor_index null.
            int parentScan;
            var master = trailer.AsPositiveInt("Master Scan Number:");
            if (master.HasValue) parentScan = master.Value;
            else parentScan = GetParentFromScanString(filterKey);

            if (parentScan > 0 && scanNumberToOrdinal.TryGetValue(parentScan, out var p))
            {
                rec.PrecursorIndex = p;
                rec.PrecursorId = ConstructSpectrumTitle((int)Device.MS, 1, parentScan);
            }

            var reaction = GetReaction(scanEvent, scanNumber);
            if (reaction == null) return;
            rec.HasReaction = true;

            double? isolationWidth = reaction.IsolationWidth;
            if (isolationWidth < 0) isolationWidth = null;

            rec.IsolationTarget = (float)reaction.PrecursorMass;
            if (isolationWidth != null)
            {
                var offset = isolationWidth.Value / 2 + reaction.IsolationWidthOffset;
                rec.IsolationLowerOffset = (float)(isolationWidth.Value - offset);
                rec.IsolationUpperOffset = (float)offset;
            }

            if (OntologyMapping.DissociationTypes.TryGetValue(reaction.ActivationType, out var diss))
            {
                rec.ActivationParams.Add(new Param { Accession = diss.accession, Name = diss.name });
            }

            if (reaction.CollisionEnergyValid)
            {
                rec.ActivationParams.Add(new Param
                {
                    Accession = MzPeakCv.CollisionEnergy,
                    Name = "collision energy",
                    Float = reaction.CollisionEnergy,
                    Unit = MzPeakCv.ElectronvoltUnit
                });
            }

            int? charge = trailer.AsPositiveInt("Charge State:");
            double? monoisotopicMz = trailer.AsDouble("Monoisotopic M/Z:");
            rec.SelectedIonMz = CalculateSelectedIonMz(reaction, monoisotopicMz, isolationWidth);
            rec.ChargeState = charge;

            // Selected-ion intensity is read FROM the parent spectrum, so it is only meaningful when
            // the parent was actually emitted (a committed ordinal). A parent that was skipped/filtered
            // out or whose read fails must never force this otherwise-readable child to fail: gate on
            // the parent ordinal being known, and treat any parent-read error as "intensity unknown".
            if (rec.SelectedIonMz > ZeroDelta && rec.PrecursorIndex.HasValue)
            {
                try
                {
                    rec.SelectedIonIntensity = (float)CalculatePrecursorPeakIntensity(raw, parentScan,
                        reaction.PrecursorMass, isolationWidth,
                        ParseInput.NoPeakPicking.Contains(rec.MsLevel - 1));
                }
                catch (Exception ex)
                {
                    Log.Warn($"Scan #{scanNumber}: parent #{parentScan} intensity unreadable, " +
                             $"selected-ion intensity left null: {ex.Message}");
                }
            }
        }

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

        private static StructField PointStructField() =>
            new StructField("point",
                new DataField<ulong>("spectrum_index"),
                new DataField<double>("mz"),
                new DataField<float>("intensity"));

        // The chunk struct. In delta mode it is the 6-field struct (mz_chunk_values / intensity are
        // nullable-item lists; the writer emits no nulls, the type stays null-aware for read parity).
        // In numpress mode a 7th field (mz_numpress_linear_bytes, large_list<uint8 not null>) carries
        // the encoded m/z while mz_chunk_values is emitted NULL on every row.
        private static StructField ChunkStructField(bool numpress)
        {
            if (!numpress)
                return new StructField("chunk",
                    new DataField<ulong>("spectrum_index"),
                    new DataField<double>("mz_chunk_start"),
                    new DataField<double>("mz_chunk_end"),
                    new ListField("mz_chunk_values", new DataField<double>("item", true)),
                    new DataField<string>("chunk_encoding"),
                    new ListField("intensity", new DataField<float>("item", true)));

            return new StructField("chunk",
                new DataField<ulong>("spectrum_index"),
                new DataField<double>("mz_chunk_start"),
                new DataField<double>("mz_chunk_end"),
                new ListField("mz_chunk_values", new DataField<double>("item", true)),
                new DataField<string>("chunk_encoding"),
                new ListField("intensity", new DataField<float>("item", true)),
                new ListField("mz_numpress_linear_bytes", new DataField<byte>("item")));
        }

        private static StructField ChromDataStructField() =>
            new StructField("point",
                new DataField<ulong>("chromatogram_index"),
                new DataField<double>("time"),
                new DataField<float>("intensity"),
                new DataField<long>("ms_level"));

        private void RegisterChromDataPrefixes()
        {
            foreach (var curie in ChromDataAccessions) CollectPrefix(curie);
        }

        // The spectra_data facet contract shared by the point and chunk layouts: append a scan's
        // (mz,intensity) arrays, expose the running data-point count and temp path, and close with the
        // footer KV. The Write() path selects the implementation by the layout flag.
        private interface ISpectraDataFacet : IDisposable
        {
            string TempPath { get; }
            long PointCount { get; }
            void Append(ulong ordinal, double[] mz, float[] intensity);
            void Close(IReadOnlyDictionary<string, string> finalMetadata);
        }

        // A point-facet (spectra_data / spectra_peaks) streamed to a seekable temp file in bounded row
        // groups. Staging buffers accumulate per scan and flush at the cap; the final residual buffer is
        // flushed by Close, which also attaches the footer KV before disposing the writer.
        private sealed class PointFacetStream : IDisposable, ISpectraDataFacet
        {
            public string TempPath { get; }
            private readonly ParquetSchema _schema;
            private readonly DataField _idx, _mz, _int;
            private readonly int _cap;
            private readonly long _byteCap;
            private long _bufferedBytes;
            private FileStream _sink;
            private MzPeakParquet.Handle _handle;
            private readonly List<ulong> _bIdx = new List<ulong>();
            private readonly List<double> _bMz = new List<double>();
            private readonly List<float> _bInt = new List<float>();

            // Approximate uncompressed bytes per point row: u64 spectrum_index + f64 mz + f32 intensity.
            private const int PointRowBytes = 8 + 8 + 4;

            public long PointCount { get; private set; }

            public PointFacetStream(int cap, long byteCap)
            {
                _cap = cap;
                _byteCap = byteCap;
                TempPath = Path.GetTempFileName();
                _schema = new ParquetSchema(PointStructField());
                _idx = Leaf(_schema, "point/spectrum_index");
                _mz = Leaf(_schema, "point/mz");
                _int = Leaf(_schema, "point/intensity");
                try
                {
                    _sink = new FileStream(TempPath, FileMode.Create, FileAccess.Write);
                    _handle = MzPeakParquet.OpenAsync(_sink, _schema, null).GetAwaiter().GetResult();
                }
                catch
                {
                    // Self-clean a partially-opened facet: the caller never receives a reference to dispose,
                    // so release the sink and delete the temp file here before the exception propagates.
                    _handle?.Dispose();
                    _sink?.Dispose();
                    TryDelete(TempPath);
                    throw;
                }
            }

            // The row + byte caps are HARD upper bounds on the in-memory buffer: a single scan's points
            // are split across as many row groups as needed (a scan MAY span row groups in the point
            // layout), filling the remaining capacity, flushing at whichever cap is reached first, then
            // continuing with the same ordinal.
            public void Append(ulong ordinal, double[] mz, float[] intensity)
            {
                int i = 0;
                while (i < mz.Length)
                {
                    int byteRoom = _bufferedBytes >= _byteCap
                        ? 0
                        : (int)Math.Min(int.MaxValue, (_byteCap - _bufferedBytes) / PointRowBytes);
                    int room = Math.Min(_cap - _bIdx.Count, byteRoom);
                    int take = Math.Min(Math.Max(room, 1), mz.Length - i);
                    for (int j = 0; j < take; j++, i++)
                    {
                        _bIdx.Add(ordinal); _bMz.Add(mz[i]); _bInt.Add(intensity[i]);
                    }
                    _bufferedBytes += (long)take * PointRowBytes;
                    if (_bIdx.Count >= _cap || _bufferedBytes >= _byteCap) Flush();
                }
                PointCount += mz.Length;
            }

            private void Flush()
            {
                if (_bIdx.Count == 0) return;
                var cols = new Dictionary<DataField, (Array, int[], int[])>
                {
                    [_idx] = (_bIdx.ToArray(), null, null),
                    [_mz] = (_bMz.ToArray(), null, null),
                    [_int] = (_bInt.ToArray(), null, null)
                };
                _handle.WriteRowGroupAsync(_schema, cols).GetAwaiter().GetResult();
                _bIdx.Clear(); _bMz.Clear(); _bInt.Clear();
                _bufferedBytes = 0;
            }

            // Finalize order: flush residual buffer -> CloseAsync(final KV) disposes the writer -> dispose
            // the temp FileStream. After this returns the temp file is fully on disk.
            public void Close(IReadOnlyDictionary<string, string> finalMetadata)
            {
                Flush();
                _handle.CloseAsync(finalMetadata).GetAwaiter().GetResult();
                _handle = null;
                _sink.Dispose();
                _sink = null;
            }

            public void Dispose()
            {
                _handle?.Dispose();
                _sink?.Dispose();
            }
        }

        // A chunked spectra_data facet streamed to a seekable temp file. One chunk struct row per
        // non-empty m/z window per scan: scalar spectrum_index/start/end/encoding plus the two
        // nullable-item lists (mz_chunk_values delta-encoded, intensity verbatim). The row-group cap counts
        // chunk ROWS, not points; a scan contributes as many rows as it has windows.
        private sealed class ChunkFacetStream : IDisposable, ISpectraDataFacet
        {
            public string TempPath { get; }
            private readonly ParquetSchema _schema;
            private readonly DataField _idx, _start, _end, _enc, _mzItem, _intItem, _npkItem;
            private readonly ListField _mzList;
            private readonly int _cap;
            private readonly long _byteCap;
            private long _bufferedBytes;
            private readonly double _chunkSize;
            private readonly bool _numpress;
            private FileStream _sink;
            private MzPeakParquet.Handle _handle;

            private readonly List<ulong> _bIdx = new List<ulong>();
            private readonly List<double> _bStart = new List<double>();
            private readonly List<double> _bEnd = new List<double>();
            private readonly List<string> _bEnc = new List<string>();
            private readonly List<MzPeakParquet.LeafRow> _mzRows = new List<MzPeakParquet.LeafRow>();
            private readonly List<double> _mzVals = new List<double>();
            private readonly List<MzPeakParquet.LeafRow> _intRows = new List<MzPeakParquet.LeafRow>();
            private readonly List<float> _intVals = new List<float>();
            private readonly List<MzPeakParquet.LeafRow> _npkRows = new List<MzPeakParquet.LeafRow>();
            private readonly List<byte> _npkVals = new List<byte>();

            // Approximate uncompressed bytes for a chunk row: the four scalars (~40 B with the encoding
            // string) + 8 B per delta-encoded m/z value + 4 B per intensity + 1 B per numpress byte.
            private const int ChunkRowOverheadBytes = 40;

            public long PointCount { get; private set; }

            public ChunkFacetStream(int cap, long byteCap, double chunkSize, bool numpress)
            {
                _cap = cap;
                _byteCap = byteCap;
                _chunkSize = chunkSize;
                _numpress = numpress;
                TempPath = Path.GetTempFileName();
                _schema = new ParquetSchema(ChunkStructField(numpress));
                _idx = Leaf(_schema, "chunk/spectrum_index");
                _start = Leaf(_schema, "chunk/mz_chunk_start");
                _end = Leaf(_schema, "chunk/mz_chunk_end");
                _enc = Leaf(_schema, "chunk/chunk_encoding");
                _mzItem = Leaf(_schema, "chunk/mz_chunk_values/list/item");
                _mzList = (ListField)FindField(_schema, "chunk/mz_chunk_values");
                _intItem = Leaf(_schema, "chunk/intensity/list/item");
                _npkItem = numpress ? Leaf(_schema, "chunk/mz_numpress_linear_bytes/list/item") : null;
                try
                {
                    _sink = new FileStream(TempPath, FileMode.Create, FileAccess.Write);
                    _handle = MzPeakParquet.OpenAsync(_sink, _schema, null).GetAwaiter().GetResult();
                }
                catch
                {
                    _handle?.Dispose();
                    _sink?.Dispose();
                    TryDelete(TempPath);
                    throw;
                }
            }

            // Builds one chunk row per non-empty window. Empty scans contribute no row.
            public void Append(ulong ordinal, double[] mz, float[] intensity)
            {
                foreach (var (s, e) in MzPeakChunkCodec.Chunk(mz, _chunkSize))
                {
                    int k = e - s;
                    long rowBytes = ChunkRowOverheadBytes + 4L * k;

                    if (_numpress)
                    {
                        var win = new double[k];
                        for (int i = 0; i < k; i++) win[i] = mz[i + s];

                        _bIdx.Add(ordinal);
                        _bStart.Add(win[0]);
                        _bEnd.Add(win[k - 1]);
                        _bEnc.Add(MzPeakCv.NumpressLinear);

                        // mz_chunk_values is NULL on every numpress row (parent present, list null).
                        _mzRows.Add(MzPeakParquet.NullList(_mzList));

                        var bytes = MSNumpress.EncodeLinear(win);
                        var nLevels = new int[bytes.Length];
                        var nHas = new bool[bytes.Length];
                        for (int i = 0; i < bytes.Length; i++)
                        {
                            nLevels[i] = _npkItem.MaxDefinitionLevel; nHas[i] = true;
                            _npkVals.Add(bytes[i]);
                        }
                        _npkRows.Add(MzPeakParquet.ListOf(nLevels, nHas));
                        rowBytes += bytes.Length;
                    }
                    else
                    {
                        var slice = new double?[k];
                        for (int i = s; i < e; i++) slice[i - s] = mz[i];
                        MzPeakChunkCodec.DeltaEncode(slice, out var start, out var end, out var values);

                        _bIdx.Add(ordinal);
                        _bStart.Add(start);
                        _bEnd.Add(end);
                        _bEnc.Add(MzPeakCv.ChunkEncoding);

                        if (values.Length == 0)
                        {
                            _mzRows.Add(MzPeakParquet.EmptyList(_mzList));
                        }
                        else
                        {
                            var levels = new int[values.Length];
                            var has = new bool[values.Length];
                            for (int i = 0; i < values.Length; i++)
                            {
                                levels[i] = _mzItem.MaxDefinitionLevel; has[i] = true;
                                _mzVals.Add(values[i].Value);
                            }
                            _mzRows.Add(MzPeakParquet.ListOf(levels, has));
                        }
                        rowBytes += 8L * values.Length;
                    }

                    var iLevels = new int[k];
                    var iHas = new bool[k];
                    for (int i = 0; i < k; i++)
                    {
                        iLevels[i] = _intItem.MaxDefinitionLevel; iHas[i] = true;
                        _intVals.Add(intensity[s + i]);
                    }
                    _intRows.Add(MzPeakParquet.ListOf(iLevels, iHas));

                    PointCount += k;
                    _bufferedBytes += rowBytes;
                    if (_bIdx.Count >= _cap || _bufferedBytes >= _byteCap) Flush();
                }
            }

            private void Flush()
            {
                if (_bIdx.Count == 0) return;
                var present = _bIdx.Select(_ => true).ToArray();
                var (idxDef, _i) = MzPeakParquet.NestedLevels(_idx, present.Select(_ => MzPeakParquet.Present(_idx)).ToArray());
                var (startDef, _s) = MzPeakParquet.NestedLevels(_start, present.Select(_ => MzPeakParquet.Present(_start)).ToArray());
                var (endDef, _e) = MzPeakParquet.NestedLevels(_end, present.Select(_ => MzPeakParquet.Present(_end)).ToArray());
                var (encDef, _c) = MzPeakParquet.NestedLevels(_enc, present.Select(_ => MzPeakParquet.Present(_enc)).ToArray());
                var (mzDef, mzRep) = MzPeakParquet.NestedLevels(_mzItem, _mzRows);
                var (intDef, intRep) = MzPeakParquet.NestedLevels(_intItem, _intRows);

                var cols = new Dictionary<DataField, (Array, int[], int[])>
                {
                    [_idx] = (_bIdx.ToArray(), idxDef, null),
                    [_start] = (_bStart.ToArray(), startDef, null),
                    [_end] = (_bEnd.ToArray(), endDef, null),
                    [_enc] = (_bEnc.ToArray(), encDef, null),
                    [_mzItem] = (_mzVals.ToArray(), mzDef, mzRep),
                    [_intItem] = (_intVals.ToArray(), intDef, intRep)
                };
                if (_numpress)
                {
                    var (npkDef, npkRep) = MzPeakParquet.NestedLevels(_npkItem, _npkRows);
                    cols[_npkItem] = (_npkVals.ToArray(), npkDef, npkRep);
                }
                _handle.WriteRowGroupAsync(_schema, cols).GetAwaiter().GetResult();

                _bIdx.Clear(); _bStart.Clear(); _bEnd.Clear(); _bEnc.Clear();
                _mzRows.Clear(); _mzVals.Clear(); _intRows.Clear(); _intVals.Clear();
                _npkRows.Clear(); _npkVals.Clear();
                _bufferedBytes = 0;
            }

            public void Close(IReadOnlyDictionary<string, string> finalMetadata)
            {
                Flush();
                _handle.CloseAsync(finalMetadata).GetAwaiter().GetResult();
                _handle = null;
                _sink.Dispose();
                _sink = null;
            }

            public void Dispose()
            {
                _handle?.Dispose();
                _sink?.Dispose();
            }
        }

        // A chrom-data facet streamed to a seekable temp file. Bounded by scan count anyway; streamed for
        // uniformity with the point facets.
        private sealed class ChromDataFacetStream : IDisposable
        {
            public readonly string TempPath;
            private readonly ParquetSchema _schema;
            private readonly DataField _idx, _time, _int, _lvl;
            private readonly int _cap;
            private readonly long _byteCap;
            private long _bufferedBytes;
            private FileStream _sink;
            private MzPeakParquet.Handle _handle;
            private readonly List<ulong> _bIdx = new List<ulong>();
            private readonly List<double> _bTime = new List<double>();
            private readonly List<float> _bInt = new List<float>();
            private readonly List<long> _bLvl = new List<long>();

            // Approximate uncompressed bytes per chrom-data row: u64 index + f64 time + f32 intensity +
            // i64 ms_level.
            private const int ChromRowBytes = 8 + 8 + 4 + 8;

            public long PointCount { get; private set; }

            public ChromDataFacetStream(int cap, long byteCap)
            {
                _cap = cap;
                _byteCap = byteCap;
                TempPath = Path.GetTempFileName();
                _schema = new ParquetSchema(ChromDataStructField());
                _idx = Leaf(_schema, "point/chromatogram_index");
                _time = Leaf(_schema, "point/time");
                _int = Leaf(_schema, "point/intensity");
                _lvl = Leaf(_schema, "point/ms_level");
                try
                {
                    _sink = new FileStream(TempPath, FileMode.Create, FileAccess.Write);
                    _handle = MzPeakParquet.OpenAsync(_sink, _schema, null).GetAwaiter().GetResult();
                }
                catch
                {
                    _handle?.Dispose();
                    _sink?.Dispose();
                    TryDelete(TempPath);
                    throw;
                }
            }

            public void Append(double time, float intensity, long msLevel)
            {
                _bIdx.Add(0UL); _bTime.Add(time); _bInt.Add(intensity); _bLvl.Add(msLevel);
                PointCount++;
                _bufferedBytes += ChromRowBytes;
                if (_bIdx.Count >= _cap || _bufferedBytes >= _byteCap) Flush();
            }

            private void Flush()
            {
                if (_bIdx.Count == 0) return;
                var cols = new Dictionary<DataField, (Array, int[], int[])>
                {
                    [_idx] = (_bIdx.ToArray(), null, null),
                    [_time] = (_bTime.ToArray(), null, null),
                    [_int] = (_bInt.ToArray(), null, null),
                    [_lvl] = (_bLvl.ToArray(), null, null)
                };
                _handle.WriteRowGroupAsync(_schema, cols).GetAwaiter().GetResult();
                _bIdx.Clear(); _bTime.Clear(); _bInt.Clear(); _bLvl.Clear();
                _bufferedBytes = 0;
            }

            public void Close(IReadOnlyDictionary<string, string> finalMetadata)
            {
                Flush();
                _handle.CloseAsync(finalMetadata).GetAwaiter().GetResult();
                _handle = null;
                _sink.Dispose();
                _sink = null;
            }

            public void Dispose()
            {
                _handle?.Dispose();
                _sink?.Dispose();
            }
        }

        private byte[] BuildMetadataFacet(List<Record> records)
        {
            int n = records.Count;

            var spectrum = BuildSpectrumField();
            var scan = BuildScanField();
            var precursor = BuildPrecursorField();
            var selectedIon = BuildSelectedIonField();
            var schema = new ParquetSchema(spectrum, scan, precursor, selectedIon);

            var cols = new Dictionary<DataField, (Array, int[], int[])>();
            var presentAll = records.Select(_ => true).ToArray();

            // precursor / selected_ion are independent tables: the k-th MSn (ascending ordinal) sits
            // at row k, null-padded on rows M..N-1.
            var msnRecords = records.Where(r => r.IsMsn).OrderBy(r => r.Ordinal).ToList();

            // spectrum facet (present on all N rows)
            AddScalar(cols, schema, "spectrum/index", records.Select(r => r.Ordinal).ToArray(), presentAll);
            AddScalar(cols, schema, "spectrum/id", records.Select(r => r.Id).ToArray(), presentAll);
            AddScalar(cols, schema, "spectrum/" + Cv(MzPeakCv.MsLevel, "ms_level"),
                records.Select(r => (byte)r.MsLevel).ToArray(), presentAll);
            AddScalar(cols, schema, "spectrum/time", records.Select(r => r.Time).ToArray(), presentAll);
            AddNullableScalar(cols, schema, "spectrum/" + Cv(MzPeakCv.ScanPolarity, "scan_polarity"),
                records.Select(r => r.Polarity).ToList());
            AddScalar(cols, schema, "spectrum/" + Cv(MzPeakCv.SpectrumRepresentation, "spectrum_representation"),
                records.Select(r => r.Representation).ToArray(), presentAll);
            AddScalar(cols, schema, "spectrum/" + Cv(MzPeakCv.SpectrumType, "spectrum_type"),
                records.Select(r => r.SpectrumType).ToArray(), presentAll);
            AddNullableScalar(cols, schema, "spectrum/" + Cv(MzPeakCv.LowestObservedMz, "lowest_observed_mz", MzPeakCv.MzUnit),
                records.Select(r => r.LowestMz).ToList());
            AddNullableScalar(cols, schema, "spectrum/" + Cv(MzPeakCv.HighestObservedMz, "highest_observed_mz", MzPeakCv.MzUnit),
                records.Select(r => r.HighestMz).ToList());
            AddScalar(cols, schema, "spectrum/" + Cv(MzPeakCv.NumberOfDataPoints, "number_of_data_points"),
                records.Select(r => r.DataPointCount).ToArray(), presentAll);

            // number_of_peaks: present only where peaks were written (leaf-null otherwise).
            var npkLeaf = Leaf(schema, "spectrum/" + Cv(MzPeakCv.NumberOfPeaks, "number_of_peaks"));
            var npkRows = records.Select(r => r.PeakCount.HasValue
                ? MzPeakParquet.Present(npkLeaf)
                : MzPeakParquet.AtLevel(npkLeaf.MaxDefinitionLevel - 1, false)).ToArray();
            var (npkDef, _) = MzPeakParquet.NestedLevels(npkLeaf, npkRows);
            cols[npkLeaf] = (records.Where(r => r.PeakCount.HasValue).Select(r => r.PeakCount.Value).ToArray(), npkDef, null);

            AddNullableScalar(cols, schema, "spectrum/" + Cv(MzPeakCv.BasePeakMz, "base_peak_mz", MzPeakCv.MzUnit),
                records.Select(r => r.BasePeakMz).ToList());
            AddNullableScalar(cols, schema, "spectrum/" + Cv(MzPeakCv.BasePeakIntensity, "base_peak_intensity", MzPeakCv.CountsUnit),
                records.Select(r => r.BasePeakIntensity).ToList());
            AddScalar(cols, schema, "spectrum/" + Cv(MzPeakCv.TotalIonCurrent, "total_ion_current", MzPeakCv.CountsUnit),
                records.Select(r => r.TotalIonCurrent).ToArray(), presentAll);

            AddNullLeafScalar(cols, schema, "spectrum/data_processing_ref", n);
            AddParamList(cols, schema, "spectrum/parameters", records.Select(r => r.SpectrumParams).ToList(), presentAll);
            AddEmptyListEveryRow(cols, schema, "spectrum/auxiliary_arrays", n);
            AddScalar(cols, schema, "spectrum/number_of_auxiliary_arrays",
                records.Select(_ => 0u).ToArray(), presentAll);
            AddEmptyListEveryRow(cols, schema, "spectrum/mz_delta_model", n);

            // scan facet (present on all N rows)
            AddScalar(cols, schema, "scan/source_index", records.Select(r => r.Ordinal).ToArray(), presentAll);
            AddScalar(cols, schema, "scan/scan_index", records.Select(r => r.Ordinal).ToArray(), presentAll);
            AddScalar(cols, schema, "scan/" + Cv(MzPeakCv.ScanStartTime, "scan_start_time", MzPeakCv.MinuteUnit),
                records.Select(r => r.ScanStartTime).ToArray(), presentAll);
            AddNullableScalar(cols, schema, "scan/" + Cv(MzPeakCv.PresetScanConfiguration, "preset_scan_configuration"),
                records.Select(r => r.PresetScanConfiguration).ToList());
            AddScalar(cols, schema, "scan/" + Cv(MzPeakCv.FilterString, "filter_string"),
                records.Select(r => r.FilterString).ToArray(), presentAll);
            AddNullableScalar(cols, schema, "scan/" + Cv(MzPeakCv.IonInjectionTime, "ion_injection_time", MzPeakCv.MillisecondUnit),
                records.Select(r => r.IonInjectionTime).ToList());
            AddNullableScalar(cols, schema, "scan/ion_mobility_value",
                records.Select(_ => (double?)null).ToList());
            AddNullLeafScalar(cols, schema, "scan/ion_mobility_type", n);
            AddScalar(cols, schema, "scan/instrument_configuration_ref",
                records.Select(r => r.InstrumentConfigRef).ToArray(), presentAll);
            AddNullLeafScalar(cols, schema, "scan/spectrum_reference", n);
            AddParamList(cols, schema, "scan/parameters", records.Select(_ => new List<Param>()).ToList(), presentAll);
            AddScanWindows(cols, schema, records);

            // precursor facet (present on rows 0..M-1, null on M..N-1)
            AddMsnScalar(cols, schema, "precursor/source_index", n, msnRecords.Select(r => r.Ordinal).ToArray());
            AddMsnPrecursorIndex(cols, schema, "precursor/precursor_index", n, msnRecords);
            AddMsnString(cols, schema, "precursor/precursor_id", n, msnRecords.Select(r => r.PrecursorId).ToArray());
            AddMsnNullable(cols, schema, "precursor/isolation_window/" + Cv(MzPeakCv.IsolationWindowTargetMz, "isolation_window_target_mz"),
                n, msnRecords.Select(r => r.IsolationTarget).ToArray());
            AddMsnNullable(cols, schema, "precursor/isolation_window/" + Cv(MzPeakCv.IsolationWindowLowerOffset, "isolation_window_lower_offset", MzPeakCv.MzUnit),
                n, msnRecords.Select(r => r.IsolationLowerOffset).ToArray());
            AddMsnNullable(cols, schema, "precursor/isolation_window/" + Cv(MzPeakCv.IsolationWindowUpperOffset, "isolation_window_upper_offset", MzPeakCv.MzUnit),
                n, msnRecords.Select(r => r.IsolationUpperOffset).ToArray());
            AddMsnParamList(cols, schema, "precursor/isolation_window/parameters", n, msnRecords.Select(_ => new List<Param>()).ToList());
            AddMsnParamList(cols, schema, "precursor/activation/parameters", n, msnRecords.Select(r => r.ActivationParams).ToList());

            // selected_ion facet (present on rows 0..M-1, null on M..N-1)
            AddMsnScalar(cols, schema, "selected_ion/source_index", n, msnRecords.Select(r => r.Ordinal).ToArray());
            AddMsnPrecursorIndex(cols, schema, "selected_ion/precursor_index", n, msnRecords);
            AddMsnNullable(cols, schema, "selected_ion/" + Cv(MzPeakCv.SelectedIonMz, "selected_ion_mz", MzPeakCv.MzUnit),
                n, msnRecords.Select(r => r.SelectedIonMz).ToArray());
            AddMsnNullable(cols, schema, "selected_ion/" + Cv(MzPeakCv.ChargeState, "charge_state"),
                n, msnRecords.Select(r => r.ChargeState).ToArray());
            AddMsnNullable(cols, schema, "selected_ion/" + Cv(MzPeakCv.SelectedIonIntensity, "intensity", MzPeakCv.CountsUnit),
                n, msnRecords.Select(r => r.SelectedIonIntensity).ToArray());

            // Ion-mobility columns exist to match the selected_ion struct shape; the Thermo RAW path
            // carries no per-selected-ion mobility value, so they stay leaf-null on every MSn row.
            AddMsnNullable(cols, schema, "selected_ion/ion_mobility_value", n, msnRecords.Select(_ => (double?)null).ToArray());
            AddMsnString(cols, schema, "selected_ion/ion_mobility_type", n, msnRecords.Select(_ => (string)null).ToArray());
            AddMsnParamList(cols, schema, "selected_ion/parameters", n, msnRecords.Select(_ => new List<Param>()).ToList());

            var numpress = !ParseInput.MzPeakPointLayout && ParseInput.MzPeakNumpress;
            var arrayIndex = ParseInput.MzPeakPointLayout
                ? PointDataArrayIndex
                : (numpress ? NumpressSpectrumArrayIndex : ChunkedSpectrumArrayIndex);
            var custom = new Dictionary<string, string>
            {
                ["spectrum_count"] = n.ToString(),
                ["spectrum_data_point_count"] = "0",
                ["spectrum_array_index"] = arrayIndex
            };
            AddMetadataBlocks(custom, records);

            return WriteFacet(schema, custom, cols);
        }

        private StructField BuildSpectrumField()
        {
            return new StructField("spectrum",
                new DataField<ulong>("index", true),
                new DataField<string>("id", true),
                new DataField<byte>(Cv(MzPeakCv.MsLevel, "ms_level"), true),
                new DataField<double>("time", true),
                new DataField<sbyte>(Cv(MzPeakCv.ScanPolarity, "scan_polarity"), true),
                new DataField<string>(Cv(MzPeakCv.SpectrumRepresentation, "spectrum_representation"), true),
                new DataField<string>(Cv(MzPeakCv.SpectrumType, "spectrum_type"), true),
                new DataField<double>(Cv(MzPeakCv.LowestObservedMz, "lowest_observed_mz", MzPeakCv.MzUnit), true),
                new DataField<double>(Cv(MzPeakCv.HighestObservedMz, "highest_observed_mz", MzPeakCv.MzUnit), true),
                new DataField<ulong>(Cv(MzPeakCv.NumberOfDataPoints, "number_of_data_points"), true),
                new DataField<ulong>(Cv(MzPeakCv.NumberOfPeaks, "number_of_peaks"), true),
                new DataField<double>(Cv(MzPeakCv.BasePeakMz, "base_peak_mz", MzPeakCv.MzUnit), true),
                new DataField<float>(Cv(MzPeakCv.BasePeakIntensity, "base_peak_intensity", MzPeakCv.CountsUnit), true),
                new DataField<float>(Cv(MzPeakCv.TotalIonCurrent, "total_ion_current", MzPeakCv.CountsUnit), true),
                new DataField<string>("data_processing_ref", true),
                new ListField("parameters", MzPeakParquet.BuildParamField("item")),
                new ListField("auxiliary_arrays", BuildAuxArrayField("item")),
                new DataField<uint>("number_of_auxiliary_arrays", true),
                new ListField("mz_delta_model", new DataField<double>("item", true)));
        }

        private StructField BuildScanField()
        {
            var window = new StructField("item",
                new DataField<float>(Cv(MzPeakCv.ScanWindowLowerLimit, "scan_window_lower_limit", MzPeakCv.MzUnit), true),
                new DataField<float>(Cv(MzPeakCv.ScanWindowUpperLimit, "scan_window_upper_limit", MzPeakCv.MzUnit), true),
                new ListField("parameters", MzPeakParquet.BuildParamField("item")));
            return new StructField("scan",
                new DataField<ulong>("source_index", true),
                new DataField<ulong>("scan_index", true),
                new DataField<float>(Cv(MzPeakCv.ScanStartTime, "scan_start_time", MzPeakCv.MinuteUnit), true),
                new DataField<uint>(Cv(MzPeakCv.PresetScanConfiguration, "preset_scan_configuration"), true),
                new DataField<string>(Cv(MzPeakCv.FilterString, "filter_string"), true),
                new DataField<float>(Cv(MzPeakCv.IonInjectionTime, "ion_injection_time", MzPeakCv.MillisecondUnit), true),
                new DataField<double>("ion_mobility_value", true),
                new DataField<string>("ion_mobility_type", true),
                new DataField<uint>("instrument_configuration_ref", true),
                new DataField<string>("spectrum_reference", true),
                new ListField("parameters", MzPeakParquet.BuildParamField("item")),
                new ListField("scan_windows", window));
        }

        private StructField BuildPrecursorField()
        {
            var isolationWindow = new StructField("isolation_window",
                new DataField<float>(Cv(MzPeakCv.IsolationWindowTargetMz, "isolation_window_target_mz"), true),
                new DataField<float>(Cv(MzPeakCv.IsolationWindowLowerOffset, "isolation_window_lower_offset", MzPeakCv.MzUnit), true),
                new DataField<float>(Cv(MzPeakCv.IsolationWindowUpperOffset, "isolation_window_upper_offset", MzPeakCv.MzUnit), true),
                new ListField("parameters", MzPeakParquet.BuildParamField("item")));
            var activation = new StructField("activation",
                new ListField("parameters", MzPeakParquet.BuildParamField("item")));
            return new StructField("precursor",
                new DataField<ulong>("source_index", true),
                new DataField<ulong>("precursor_index", true),
                new DataField<string>("precursor_id", true),
                isolationWindow,
                activation);
        }

        private StructField BuildSelectedIonField()
        {
            return new StructField("selected_ion",
                new DataField<ulong>("source_index", true),
                new DataField<ulong>("precursor_index", true),
                new DataField<double>(Cv(MzPeakCv.SelectedIonMz, "selected_ion_mz", MzPeakCv.MzUnit), true),
                new DataField<int>(Cv(MzPeakCv.ChargeState, "charge_state"), true),
                new DataField<float>(Cv(MzPeakCv.SelectedIonIntensity, "intensity", MzPeakCv.CountsUnit), true),
                new DataField<double>("ion_mobility_value", true),
                new DataField<string>("ion_mobility_type", true),
                new ListField("parameters", MzPeakParquet.BuildParamField("item")));
        }

        private string Cv(string accession, string label, string unit = null)
        {
            CollectPrefix(accession);
            if (unit != null) CollectPrefix(unit);
            return MzPeakParquet.CvColumn(accession, label, unit);
        }

        private void CollectPrefix(string curie)
        {
            if (string.IsNullOrEmpty(curie)) return;
            var i = curie.IndexOf(':');
            if (i > 0) _cvPrefixes.Add(curie.Substring(0, i));
        }

        private static void AddScalar(IDictionary<DataField, (Array, int[], int[])> cols, ParquetSchema schema,
            string path, Array values, bool[] present)
        {
            var leaf = Leaf(schema, path);
            var rows = present.Select(p => p ? MzPeakParquet.Present(leaf) : MzPeakParquet.Absent()).ToArray();
            var (def, _r) = MzPeakParquet.NestedLevels(leaf, rows);
            cols[leaf] = (values, def, null);
        }

        // A scalar leaf present on every row's owning struct but whose VALUE may be absent: a null
        // value emits the leaf-null definition level (one below the present level) and contributes no
        // value slot, so a genuinely-absent value is encoded as null rather than zero.
        private static void AddNullableScalar<T>(IDictionary<DataField, (Array, int[], int[])> cols,
            ParquetSchema schema, string path, IReadOnlyList<T?> values) where T : struct
        {
            var leaf = Leaf(schema, path);
            var rows = new List<MzPeakParquet.LeafRow>();
            var present = new List<T>();
            foreach (var v in values)
            {
                if (v.HasValue) { rows.Add(MzPeakParquet.Present(leaf)); present.Add(v.Value); }
                else rows.Add(MzPeakParquet.AtLevel(leaf.MaxDefinitionLevel - 1, false));
            }
            var (def, _) = MzPeakParquet.NestedLevels(leaf, rows);
            cols[leaf] = (present.ToArray(), def, null);
        }

        // A scalar leaf whose owning struct is present on every row but whose value is null on every
        // row (leaf-null def level, no value slot).
        private static void AddNullLeafScalar(IDictionary<DataField, (Array, int[], int[])> cols,
            ParquetSchema schema, string path, int n)
        {
            var leaf = Leaf(schema, path);
            var rows = Enumerable.Range(0, n)
                .Select(_ => MzPeakParquet.AtLevel(leaf.MaxDefinitionLevel - 1, false)).ToArray();
            var (def, _) = MzPeakParquet.NestedLevels(leaf, rows);
            cols[leaf] = (EmptyArray(leaf.ClrType), def, null);
        }

        // A present-but-empty list on every row of a multi-row facet (the owning struct is present,
        // the list value is defined, no elements). Used for spectrum auxiliary_arrays / mz_delta_model.
        private void AddEmptyListEveryRow(IDictionary<DataField, (Array, int[], int[])> cols,
            ParquetSchema schema, string listPath, int n)
        {
            var list = (ListField)FindField(schema, listPath);
            foreach (var leaf in schema.GetDataFields())
            {
                if (!leaf.Path.ToString().StartsWith(listPath + "/")) continue;
                var rows = Enumerable.Range(0, n).Select(_ => MzPeakParquet.EmptyList(list)).ToArray();
                var (def, rep) = MzPeakParquet.NestedLevels(leaf, rows);
                cols[leaf] = (EmptyArray(leaf.ClrType), def, rep);
            }
        }

        // A scalar leaf inside a top-level struct present only on the first M rows (the MSn count),
        // null on rows M..N-1. values has length M (one per MSn).
        private static void AddMsnScalar(IDictionary<DataField, (Array, int[], int[])> cols, ParquetSchema schema,
            string path, int n, Array values)
        {
            var leaf = Leaf(schema, path);
            int m = values.Length;
            var rows = Enumerable.Range(0, n)
                .Select(i => i < m ? MzPeakParquet.Present(leaf) : MzPeakParquet.Absent()).ToArray();
            var (def, _) = MzPeakParquet.NestedLevels(leaf, rows);
            cols[leaf] = (values, def, null);
        }

        private static void AddMsnString(IDictionary<DataField, (Array, int[], int[])> cols, ParquetSchema schema,
            string path, int n, string[] values)
        {
            var leaf = Leaf(schema, path);
            int m = values.Length;
            var rows = new List<MzPeakParquet.LeafRow>();
            var present = new List<string>();
            for (int i = 0; i < n; i++)
            {
                if (i >= m) rows.Add(MzPeakParquet.Absent());
                else if (values[i] != null) { rows.Add(MzPeakParquet.Present(leaf)); present.Add(values[i]); }
                else rows.Add(MzPeakParquet.AtLevel(leaf.MaxDefinitionLevel - 1, false));
            }
            var (def, _) = MzPeakParquet.NestedLevels(leaf, rows);
            cols[leaf] = (present.ToArray(), def, null);
        }

        private static void AddMsnPrecursorIndex(IDictionary<DataField, (Array, int[], int[])> cols,
            ParquetSchema schema, string path, int n, List<Record> msn)
        {
            var leaf = Leaf(schema, path);
            int m = msn.Count;
            var rows = new List<MzPeakParquet.LeafRow>();
            var present = new List<ulong>();
            for (int i = 0; i < n; i++)
            {
                if (i >= m) rows.Add(MzPeakParquet.Absent());
                else if (msn[i].PrecursorIndex.HasValue)
                { rows.Add(MzPeakParquet.Present(leaf)); present.Add(msn[i].PrecursorIndex.Value); }
                else rows.Add(MzPeakParquet.AtLevel(leaf.MaxDefinitionLevel - 1, false));
            }
            var (def, _) = MzPeakParquet.NestedLevels(leaf, rows);
            cols[leaf] = (present.ToArray(), def, null);
        }

        // A nullable scalar leaf on the MSn struct (present on the first M rows). Genuine null values
        // emit leaf-null one level below present; rows past M (i >= M) sit at def-level 0 (struct absent).
        private static void AddMsnNullable<T>(IDictionary<DataField, (Array, int[], int[])> cols,
            ParquetSchema schema, string path, int n, T?[] values) where T : struct
        {
            var leaf = Leaf(schema, path);
            int m = values.Length;
            var rows = new List<MzPeakParquet.LeafRow>();
            var present = new List<T>();
            for (int i = 0; i < n; i++)
            {
                if (i >= m) rows.Add(MzPeakParquet.Absent());
                else if (values[i].HasValue) { rows.Add(MzPeakParquet.Present(leaf)); present.Add(values[i].Value); }
                else rows.Add(MzPeakParquet.AtLevel(leaf.MaxDefinitionLevel - 1, false));
            }
            var (def, _) = MzPeakParquet.NestedLevels(leaf, rows);
            cols[leaf] = (present.ToArray(), def, null);
        }

        // PARAM list inside a top-level struct present only on the first M rows. perRow has length M.
        private void AddMsnParamList(IDictionary<DataField, (Array, int[], int[])> cols, ParquetSchema schema,
            string listPath, int n, List<List<Param>> perRow)
        {
            int m = perRow.Count;
            var structPresent = Enumerable.Range(0, n).Select(i => i < m).ToArray();
            var padded = new List<List<Param>>();
            for (int i = 0; i < n; i++) padded.Add(i < m ? perRow[i] : new List<Param>());
            AddParamList(cols, schema, listPath, padded, structPresent);
        }

        private void AddParamList(IDictionary<DataField, (Array, int[], int[])> cols, ParquetSchema schema,
            string listPath, List<List<Param>> perRow, bool[] structPresent)
        {
            var list = (ListField)FindField(schema, listPath);
            var accLeaf = Leaf(schema, listPath + "/list/item/accession");
            var nameLeaf = Leaf(schema, listPath + "/list/item/name");
            var unitLeaf = Leaf(schema, listPath + "/list/item/unit");
            var intLeaf = Leaf(schema, listPath + "/list/item/value/integer");
            var fltLeaf = Leaf(schema, listPath + "/list/item/value/float");
            var strLeaf = Leaf(schema, listPath + "/list/item/value/string");
            var boolLeaf = Leaf(schema, listPath + "/list/item/value/boolean");

            var accRows = new List<MzPeakParquet.LeafRow>();
            var nameRows = new List<MzPeakParquet.LeafRow>();
            var unitRows = new List<MzPeakParquet.LeafRow>();
            var intRows = new List<MzPeakParquet.LeafRow>();
            var fltRows = new List<MzPeakParquet.LeafRow>();
            var strRows = new List<MzPeakParquet.LeafRow>();
            var boolRows = new List<MzPeakParquet.LeafRow>();

            var accVals = new List<string>();
            var nameVals = new List<string>();
            var unitVals = new List<string>();
            var intVals = new List<long>();
            var fltVals = new List<double>();
            var strVals = new List<string>();
            var boolVals = new List<bool>();

            for (int row = 0; row < perRow.Count; row++)
            {
                if (!structPresent[row])
                {
                    accRows.Add(MzPeakParquet.NullList()); nameRows.Add(MzPeakParquet.NullList());
                    unitRows.Add(MzPeakParquet.NullList()); intRows.Add(MzPeakParquet.NullList());
                    fltRows.Add(MzPeakParquet.NullList()); strRows.Add(MzPeakParquet.NullList());
                    boolRows.Add(MzPeakParquet.NullList());
                    continue;
                }

                var items = perRow[row];
                if (items.Count == 0)
                {
                    accRows.Add(MzPeakParquet.EmptyList(list)); nameRows.Add(MzPeakParquet.EmptyList(list));
                    unitRows.Add(MzPeakParquet.EmptyList(list)); intRows.Add(MzPeakParquet.EmptyList(list));
                    fltRows.Add(MzPeakParquet.EmptyList(list)); strRows.Add(MzPeakParquet.EmptyList(list));
                    boolRows.Add(MzPeakParquet.EmptyList(list));
                    continue;
                }

                foreach (var it in items) { CollectPrefix(it.Accession); CollectPrefix(it.Unit); }
                AddListLeaf(accRows, accVals, accLeaf, items, p => p.Accession);
                AddListLeaf(nameRows, nameVals, nameLeaf, items, p => p.Name);
                AddListLeaf(unitRows, unitVals, unitLeaf, items, p => p.Unit);
                AddListLeafNumStruct(intRows, intVals, intLeaf, items, p => p.Integer);
                AddListLeafNumStruct(fltRows, fltVals, fltLeaf, items, p => p.Float);
                AddListLeaf(strRows, strVals, strLeaf, items, p => p.String);
                AddListLeafNumStruct(boolRows, boolVals, boolLeaf, items, p => p.Boolean);
            }

            Emit(cols, accLeaf, accVals.ToArray(), accRows);
            Emit(cols, nameLeaf, nameVals.ToArray(), nameRows);
            Emit(cols, unitLeaf, unitVals.ToArray(), unitRows);
            Emit(cols, intLeaf, intVals.ToArray(), intRows);
            Emit(cols, fltLeaf, fltVals.ToArray(), fltRows);
            Emit(cols, strLeaf, strVals.ToArray(), strRows);
            Emit(cols, boolLeaf, boolVals.ToArray(), boolRows);
        }

        private static void AddListLeaf(List<MzPeakParquet.LeafRow> rows, List<string> vals, DataField leaf,
            List<Param> items, Func<Param, string> sel)
        {
            var levels = new int[items.Count];
            var has = new bool[items.Count];
            for (int i = 0; i < items.Count; i++)
            {
                var v = sel(items[i]);
                if (v != null) { vals.Add(v); levels[i] = leaf.MaxDefinitionLevel; has[i] = true; }
                else { levels[i] = leaf.MaxDefinitionLevel - 1; has[i] = false; }
            }
            rows.Add(MzPeakParquet.ListOf(levels, has));
        }

        private static void AddListLeafNumStruct<T>(List<MzPeakParquet.LeafRow> rows, List<T> vals, DataField leaf,
            List<Param> items, Func<Param, T?> sel) where T : struct
        {
            var levels = new int[items.Count];
            var has = new bool[items.Count];
            for (int i = 0; i < items.Count; i++)
            {
                var v = sel(items[i]);
                if (v.HasValue) { vals.Add(v.Value); levels[i] = leaf.MaxDefinitionLevel; has[i] = true; }
                else { levels[i] = leaf.MaxDefinitionLevel - 1; has[i] = false; }
            }
            rows.Add(MzPeakParquet.ListOf(levels, has));
        }

        private static void Emit(IDictionary<DataField, (Array, int[], int[])> cols, DataField leaf,
            Array values, List<MzPeakParquet.LeafRow> rows)
        {
            var (def, rep) = MzPeakParquet.NestedLevels(leaf, rows);
            cols[leaf] = (values, def, rep);
        }

        private void AddScanWindows(IDictionary<DataField, (Array, int[], int[])> cols, ParquetSchema schema,
            List<Record> records)
        {
            var lowerLeaf = Leaf(schema, "scan/scan_windows/list/item/" + Cv(MzPeakCv.ScanWindowLowerLimit, "scan_window_lower_limit", MzPeakCv.MzUnit));
            var upperLeaf = Leaf(schema, "scan/scan_windows/list/item/" + Cv(MzPeakCv.ScanWindowUpperLimit, "scan_window_upper_limit", MzPeakCv.MzUnit));

            var lowerRows = new List<MzPeakParquet.LeafRow>();
            var upperRows = new List<MzPeakParquet.LeafRow>();
            var lowerVals = new List<float>();
            var upperVals = new List<float>();

            for (int row = 0; row < records.Count; row++)
            {
                lowerRows.Add(MzPeakParquet.ListOf(new[] { lowerLeaf.MaxDefinitionLevel }, new[] { true }));
                upperRows.Add(MzPeakParquet.ListOf(new[] { upperLeaf.MaxDefinitionLevel }, new[] { true }));
                lowerVals.Add(records[row].WindowLower);
                upperVals.Add(records[row].WindowUpper);
            }

            Emit(cols, lowerLeaf, lowerVals.ToArray(), lowerRows);
            Emit(cols, upperLeaf, upperVals.ToArray(), upperRows);

            // The per-window parameters list is present-but-empty: one window element per row, its
            // parameters list defined with no entries.
            const string winParams = "scan/scan_windows/list/item/parameters";
            var scanWindows = (ListField)FindField(schema, "scan/scan_windows");
            var windowItem = (StructField)scanWindows.Item;
            var winParamsList = (ListField)windowItem.Fields.First(f => f.Name == "parameters");
            foreach (var leaf in schema.GetDataFields())
            {
                if (!leaf.Path.ToString().StartsWith(winParams + "/")) continue;
                var rows = Enumerable.Range(0, records.Count)
                    .Select(_ => MzPeakParquet.EmptyList(winParamsList))
                    .ToList();
                Emit(cols, leaf, EmptyArray(leaf.ClrType), rows);
            }
        }

        private static Field FindField(ParquetSchema schema, string path)
        {
            var parts = path.Split('/');
            Field current = schema.Fields.First(f => f.Name == parts[0]);
            for (int i = 1; i < parts.Length; i++)
            {
                current = ((StructField)current).Fields.First(f => f.Name == parts[i]);
            }
            return current;
        }

        private static byte[] WriteFacet(ParquetSchema schema, IReadOnlyDictionary<string, string> meta,
            IDictionary<DataField, (Array, int[], int[])> cols)
        {
            using (var ms = new MemoryStream())
            {
                MzPeakParquet.WriteAsync(ms, schema, meta, cols).GetAwaiter().GetResult();
                return ms.ToArray();
            }
        }

        // File-level metadata blocks shared verbatim between the parquet footer KV and the index
        // metadata{}. Built once after all CV-named columns/params are emitted so the generated
        // cv_list covers exactly the collected CV-prefix set.
        private JObject _metadataBlocks;

        private void AddMetadataBlocks(IDictionary<string, string> custom, List<Record> records)
        {
            var instruments = BuildInstrumentConfigurations();
            var software = BuildSoftwareList();
            var dataProcessing = BuildDataProcessingList();
            var fileDescription = BuildFileDescription(records);
            var run = BuildRun();

            // cv_list is generated LAST: every accession/unit routed through Cv() or a param helper
            // has been recorded in _cvPrefixes by this point.
            var cvList = BuildCvList();

            _metadataBlocks = new JObject
            {
                ["version"] = MzPeakVersion,
                ["cv_list"] = cvList,
                ["file_description"] = fileDescription,
                ["instrument_configuration_list"] = instruments,
                ["software_list"] = software,
                ["data_processing_method_list"] = dataProcessing,
                ["run"] = run,
                ["sample_list"] = new JArray(),
                ["scan_settings_list"] = new JArray()
            };

            custom["cv_list"] = Compact(cvList);
            custom["file_description"] = Compact(fileDescription);
            custom["instrument_configuration_list"] = Compact(instruments);
            custom["software_list"] = Compact(software);
            custom["data_processing_method_list"] = Compact(dataProcessing);
            custom["run"] = Compact(run);
            custom["sample_list"] = "[]";
            custom["scan_settings_list"] = "[]";
        }

        private static string Compact(JToken token) =>
            token.ToString(Newtonsoft.Json.Formatting.None);

        private JArray BuildCvList()
        {
            var defs = new Dictionary<string, (string version, string fullName, string uri)>
            {
                ["MS"] = (MsCvVersion, "PSI-MS controlled vocabulary",
                    "https://raw.githubusercontent.com/HUPO-PSI/psi-ms-CV/master/psi-ms.obo"),
                ["UO"] = (UoCvVersion, "Unit Ontology", "http://purl.obolibrary.org/obo/uo.obo")
            };

            var list = new JArray();
            foreach (var prefix in _cvPrefixes.OrderBy(p => p))
            {
                defs.TryGetValue(prefix, out var d);
                list.Add(new JObject
                {
                    ["id"] = prefix,
                    ["version"] = d.version ?? "unknown",
                    ["full_name"] = d.fullName ?? prefix,
                    ["uri"] = d.uri ?? ""
                });
            }
            return list;
        }

        private JArray BuildInstrumentConfigurations()
        {
            OntologyMapping.UpdateFTMSDefinition(_instrumentModel);
            var model = OntologyMapping.GetInstrumentModel(_instrumentModel);
            var detectors = OntologyMapping.GetDetectors(model.accession);

            var list = new JArray();
            for (int i = 0; i < _analyzerOrder.Count; i++)
            {
                var ionization = OntologyMapping.IonizationTypes.TryGetValue(_ionizationOrder[i], out var ion)
                    ? ion
                    : OntologyMapping.IonizationTypes[IonizationModeType.Any];
                var analyzer = OntologyMapping.MassAnalyzerTypes[_analyzerOrder[i]];
                var detector = i < detectors.Count ? detectors[i] : OntologyMapping.GetDetectors("default")[0];

                var configParams = new JArray
                {
                    CvParam(model.accession, model.name, model.value),
                    CvParam(MzPeakCv.InstrumentSerialNumber, "instrument serial number", _instrumentSerial)
                };
                var components = new JArray
                {
                    Component("ionsource", 1, ionization),
                    Component("analyzer", 2, analyzer),
                    Component("detector", 3, detector)
                };
                list.Add(new JObject
                {
                    ["id"] = i,
                    ["software_reference"] = "ThermoRawFileParser",
                    ["parameters"] = configParams,
                    ["components"] = components
                });
            }
            return list;
        }

        private JObject Component(string type, int order, CVParamType cv)
        {
            return new JObject
            {
                ["component_type"] = type,
                ["order"] = order,
                ["parameters"] = new JArray { CvParam(cv.accession, cv.name, cv.value) }
            };
        }

        private JArray BuildSoftwareList()
        {
            return new JArray
            {
                new JObject
                {
                    ["id"] = "ThermoRawFileParser",
                    ["version"] = MainClass.Version,
                    ["parameters"] = new JArray { CvParam(MzPeakCv.ThermoRawFileParser, "ThermoRawFileParser", null) }
                }
            };
        }

        private JArray BuildDataProcessingList()
        {
            var methods = new JArray
            {
                new JObject
                {
                    ["order"] = 0,
                    ["software_reference"] = "ThermoRawFileParser",
                    ["parameters"] = new JArray { CvParam(MzPeakCv.FileFormatConversion, "file format conversion", null) }
                },
                new JObject
                {
                    ["order"] = 1,
                    ["software_reference"] = "ThermoRawFileParser",
                    ["parameters"] = new JArray { JParam("intensity narrowing", null, "f64 to f32", null) }
                }
            };

            if (!ParseInput.MzPeakPointLayout && ParseInput.MzPeakNumpress)
            {
                methods.Add(new JObject
                {
                    ["order"] = 2,
                    ["software_reference"] = "ThermoRawFileParser",
                    ["parameters"] = new JArray
                    {
                        CvParam(MzPeakCv.NumpressLinear, "MS-Numpress linear prediction compression", null),
                        JParam("m/z encoding", null, "lossy Numpress-linear (bounded ~5e-7 Th); intensity lossless f32", null)
                    }
                });
            }

            return new JArray
            {
                new JObject
                {
                    ["id"] = "trfp_conversion",
                    ["methods"] = methods
                }
            };
        }

        private JObject BuildFileDescription(List<Record> records)
        {
            var contents = new JArray
            {
                CvParam(MzPeakCv.Ms1Spectrum, "MS1 spectrum", null),
                CvParam(MzPeakCv.MsnSpectrum, "MSn spectrum", null)
            };
            var sourceParams = new JArray
            {
                CvParam(MzPeakCv.ThermoNativeIdFormat, "Thermo nativeID format", null),
                CvParam(MzPeakCv.ThermoRawFormat, "Thermo RAW format", null)
            };
            return new JObject
            {
                ["contents"] = contents,
                ["source_files"] = new JArray
                {
                    new JObject
                    {
                        ["id"] = "RAW1",
                        ["name"] = _sourceName,
                        ["location"] = _sourceLocation,
                        ["parameters"] = sourceParams
                    }
                }
            };
        }

        private JObject BuildRun()
        {
            return new JObject
            {
                ["id"] = Path.GetFileNameWithoutExtension(_sourceName),
                ["default_instrument_id"] = 0,
                ["default_data_processing_id"] = "trfp_conversion",
                ["default_source_file_id"] = "RAW1",
                ["start_time"] = _creationDate.ToUniversalTime()
                    .ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)
            };
        }

        // A controlled-value PARAM (accession + name, no value). Records the CV prefix so the
        // generated cv_list stays exhaustive.
        private JObject CvParam(string accession, string name, string value)
        {
            CollectPrefix(accession);
            return JParam(name, accession, string.IsNullOrEmpty(value) ? null : value, null);
        }

        // A PARAM object matching the param.json schema (name required; accession/value/unit nullable).
        private JObject JParam(string name, string accession, object value, string unit)
        {
            CollectPrefix(unit);
            return new JObject
            {
                ["name"] = name,
                ["accession"] = accession == null ? JValue.CreateNull() : new JValue(accession),
                ["value"] = value == null ? JValue.CreateNull() : JToken.FromObject(value),
                ["unit"] = unit == null ? JValue.CreateNull() : new JValue(unit)
            };
        }

        private byte[] BuildIndex(bool hasPeaks, bool hasChromatograms)
        {
            var files = new JArray
            {
                new JObject
                {
                    ["name"] = "spectra_data.parquet",
                    ["entity_type"] = "spectrum",
                    ["data_kind"] = "data arrays"
                },
                new JObject
                {
                    ["name"] = "spectra_metadata.parquet",
                    ["entity_type"] = "spectrum",
                    ["data_kind"] = "metadata"
                }
            };

            if (hasPeaks)
            {
                files.Add(new JObject
                {
                    ["name"] = "spectra_peaks.parquet",
                    ["entity_type"] = "spectrum",
                    ["data_kind"] = "peaks"
                });
            }

            if (hasChromatograms)
            {
                files.Add(new JObject
                {
                    ["name"] = "chromatograms_metadata.parquet",
                    ["entity_type"] = "chromatogram",
                    ["data_kind"] = "metadata"
                });
                files.Add(new JObject
                {
                    ["name"] = "chromatograms_data.parquet",
                    ["entity_type"] = "chromatogram",
                    ["data_kind"] = "data arrays"
                });
            }

            var metadata = _metadataBlocks != null
                ? (JObject)_metadataBlocks.DeepClone()
                : new JObject { ["version"] = MzPeakVersion };

            var index = new JObject
            {
                ["version"] = MzPeakVersion,
                ["files"] = files,
                ["metadata"] = metadata
            };

            return new UTF8Encoding(false).GetBytes(index.ToString());
        }

        private static DataField Leaf(ParquetSchema schema, string path)
        {
            foreach (var d in schema.GetDataFields())
                if (d.Path.ToString() == path) return d;
            throw new RawFileParserException($"Leaf not found: {path}");
        }
    }
}
