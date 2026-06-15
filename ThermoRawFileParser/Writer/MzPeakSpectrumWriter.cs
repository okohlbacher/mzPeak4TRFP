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

        // When set, CaptureTic skips the device TIC trace and uses the summed ScanStatistics TIC,
        // exercising the fallback path (TicSource == "summed"). Left false in normal operation.
        internal bool TestForceSummedTic;

        // Optional per-MSn override of the precursor charge state, keyed by child scan number. Returns
        // the charge to record (which may differ from the trailer's "Charge State:"), exercising the
        // non-null charge column on inputs whose trailer carries no charge. Left null in normal operation.
        internal Func<int, int?> TestChargeOverride;

        // Everything staged from a single scan, held in locals until the scan fully succeeds. A read/build
        // failure discards the whole struct: no ordinal, no rows, no precursor-map entry are committed.
        private sealed class StagedScan
        {
            public double[] Mz, PeakMz;
            public float[] Inten, PeakInten;
            public MzPeakRecord Rec;
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

            var records = new List<MzPeakRecord>();
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
                    ["chromatogram_array_index"] = MzPeakLayout.ChromatogramArrayIndex,
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

            var rec = new MzPeakRecord
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

        private void CaptureTic(IRawDataPlus raw, List<MzPeakRecord> records, List<double> time,
            List<float> intensity, List<long> msLevel)
        {
            ChromatogramSignal[] trace;
            if (TestForceSummedTic)
            {
                trace = Array.Empty<ChromatogramSignal>();
            }
            else
            {
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
            string filterKey, MzPeakRecord rec)
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
                rec.ActivationParams.Add(new MzPeakParam { Accession = diss.accession, Name = diss.name });
            }

            if (reaction.CollisionEnergyValid)
            {
                rec.ActivationParams.Add(new MzPeakParam
                {
                    Accession = MzPeakCv.CollisionEnergy,
                    Name = "collision energy",
                    Float = reaction.CollisionEnergy,
                    Unit = MzPeakCv.ElectronvoltUnit
                });
            }

            int? charge = trailer.AsPositiveInt("Charge State:");
            if (TestChargeOverride != null) charge = TestChargeOverride(scanNumber);
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

        private void RegisterChromDataPrefixes()
        {
            foreach (var curie in MzPeakLayout.ChromDataAccessions) CollectPrefix(curie);
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

        // File-level metadata blocks shared verbatim between the parquet footer KV and the index
        // metadata{}. Built once after all CV-named columns/params are emitted so the generated
        // cv_list covers exactly the collected CV-prefix set.
        private JObject _metadataBlocks;

        private void AddMetadataBlocks(IDictionary<string, string> custom, List<MzPeakRecord> records)
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
                ["version"] = MzPeakLayout.MzPeakVersion,
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
                ["MS"] = (MzPeakLayout.MsCvVersion, "PSI-MS controlled vocabulary",
                    "https://raw.githubusercontent.com/HUPO-PSI/psi-ms-CV/master/psi-ms.obo"),
                ["UO"] = (MzPeakLayout.UoCvVersion, "Unit Ontology", "http://purl.obolibrary.org/obo/uo.obo")
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

        private JObject BuildFileDescription(List<MzPeakRecord> records)
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
                : new JObject { ["version"] = MzPeakLayout.MzPeakVersion };

            var index = new JObject
            {
                ["version"] = MzPeakLayout.MzPeakVersion,
                ["files"] = files,
                ["metadata"] = metadata
            };

            return new UTF8Encoding(false).GetBytes(index.ToString());
        }
    }
}
