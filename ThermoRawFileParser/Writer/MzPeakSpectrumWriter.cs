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

        private const string SpectrumArrayIndex =
            "{\"prefix\":\"point\",\"entries\":[" +
            "{\"context\":\"spectrum\",\"path\":\"point.mz\",\"data_type\":\"MS:1000523\",\"array_type\":\"MS:1000514\"," +
            "\"array_name\":\"m/z array\",\"unit\":\"MS:1000040\",\"buffer_format\":\"point\",\"buffer_priority\":\"primary\",\"sorting_rank\":0}," +
            "{\"context\":\"spectrum\",\"path\":\"point.intensity\",\"data_type\":\"MS:1000521\",\"array_type\":\"MS:1000515\"," +
            "\"array_name\":\"intensity array\",\"unit\":\"MS:1000131\",\"buffer_format\":\"point\",\"buffer_priority\":\"primary\"}" +
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
            public sbyte Polarity;
            public string Representation;
            public string SpectrumType;
            public double LowestMz;
            public double HighestMz;
            public double BasePeakMz;
            public float BasePeakIntensity;
            public float TotalIonCurrent;
            public ulong DataPointCount;
            public ulong? PeakCount;
            public List<Param> SpectrumParams = new List<Param>();

            public float ScanStartTime;
            public string FilterString;
            public float IonInjectionTime;
            public uint InstrumentConfigRef;
            public float WindowLower;
            public float WindowUpper;

            public bool IsMsn;
            public ulong? PrecursorIndex;
            public string PrecursorId;
            public float IsolationTarget;
            public float? IsolationLowerOffset;
            public float? IsolationUpperOffset;
            public List<Param> ActivationParams = new List<Param>();
            public double SelectedIonMz;
            public int? ChargeState;
            public float? SelectedIonIntensity;
        }

        private readonly HashSet<string> _cvPrefixes = new HashSet<string>();
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

            var dataIndex = new List<ulong>();
            var dataMz = new List<double>();
            var dataIntensity = new List<float>();

            var peaksIndex = new List<ulong>();
            var peaksMz = new List<double>();
            var peaksIntensity = new List<float>();
            var peakSpectra = new HashSet<ulong>();

            var records = new List<Record>();
            var scanNumberToOrdinal = new Dictionary<int, ulong>();

            ulong ordinal = 0;

            for (var scanNumber = firstScanNumber; scanNumber <= lastScanNumber; scanNumber++)
            {
                try
                {
                    var scanFilter = raw.GetFilterForScanNumber(scanNumber);
                    int level = (int)scanFilter.MSOrder;
                    if (level > ParseInput.MaxLevel || !ParseInput.MsLevel.Contains(level)) continue;

                    var scanEvent = raw.GetScanEventForScanNumber(scanNumber);
                    var mzData = ReadMZData(raw, scanEvent, scanNumber, false, false, false);

                    var (mz, inten) = OrderedPairs(mzData.masses, mzData.intensities);
                    if (mz.Length == 0) continue;

                    for (int i = 0; i < mz.Length; i++)
                    {
                        dataIndex.Add(ordinal);
                        dataMz.Add(mz[i]);
                        dataIntensity.Add(inten[i]);
                    }

                    ulong? peakCount = null;
                    if (scanEvent.ScanData == ScanDataType.Profile && Scan.FromFile(raw, scanNumber).HasCentroidStream)
                    {
                        var peakData = ReadMZData(raw, scanEvent, scanNumber, true, false, false);
                        var (pMz, pInten) = OrderedPairs(peakData.masses, peakData.intensities);
                        if (pMz.Length > 0)
                        {
                            for (int i = 0; i < pMz.Length; i++)
                            {
                                peaksIndex.Add(ordinal);
                                peaksMz.Add(pMz[i]);
                                peaksIntensity.Add(pInten[i]);
                            }
                            peakSpectra.Add(ordinal);
                            peakCount = (ulong)pMz.Length;
                        }
                    }

                    var scanStats = raw.GetScanStatsForScanNumber(scanNumber);
                    var trailer = new ScanTrailer(raw.GetTrailerExtraInformation(scanNumber));

                    // Mirror the mzML parent-derivation bookkeeping: map each scan's filter prefix to
                    // its scan number so an MSn child can resolve its parent via the scan string.
                    var filterMatch = level == 1
                        ? null
                        : (level == 2
                            ? _filterStringIsolationMzPattern.Match(scanEvent.ToString())
                            : _filterStringParentMzPattern.Match(scanEvent.ToString()));
                    var filterKey = level == 1 ? "" : (filterMatch != null && filterMatch.Success ? filterMatch.Groups[1].Value : "");
                    _precursorScanNumbers[filterKey] = scanNumber;

                    var rec = new Record
                    {
                        Ordinal = ordinal,
                        ScanNumber = scanNumber,
                        MsLevel = level,
                        Id = ConstructSpectrumTitle((int)Device.MS, 1, scanNumber),
                        Time = raw.RetentionTimeFromScanNumber(scanNumber),
                        Polarity = scanFilter.Polarity == PolarityType.Negative ? (sbyte)-1 : (sbyte)1,
                        Representation = mzData.isCentroided ? "MS:1000127" : "MS:1000128",
                        SpectrumType = level == 1 ? "MS:1000579" : "MS:1000580",
                        LowestMz = mz[0],
                        HighestMz = mz[mz.Length - 1],
                        BasePeakMz = mzData.basePeakMass ?? 0.0,
                        BasePeakIntensity = (float)(mzData.basePeakIntensity ?? 0.0),
                        TotalIonCurrent = (float)scanStats.TIC,
                        DataPointCount = (ulong)mz.Length,
                        PeakCount = peakCount,
                        ScanStartTime = (float)raw.RetentionTimeFromScanNumber(scanNumber),
                        FilterString = scanEvent.ToString(),
                        IonInjectionTime = (float)(trailer.AsDouble("Ion Injection Time (ms):") ?? 0.0),
                        InstrumentConfigRef = AnalyzerIndex(scanFilter.MassAnalyzer, scanFilter.IonizationMode),
                        WindowLower = (float)scanStats.LowMass,
                        WindowUpper = (float)scanStats.HighMass
                    };

                    if (level >= 2)
                    {
                        BuildPrecursor(raw, scanNumber, scanEvent, scanFilter, trailer, scanNumberToOrdinal,
                            filterKey, rec);
                    }

                    records.Add(rec);
                    scanNumberToOrdinal[scanNumber] = ordinal;
                    ordinal++;
                }
                catch (Exception ex)
                {
                    Log.Error($"Scan #{scanNumber} cannot be processed because of the following exception: {ex.Message}");
                    Log.Debug($"{ex.StackTrace}\n{ex.InnerException}");
                    ParseInput.NewError();
                }
            }

            if (ordinal == 0)
            {
                throw new RawFileParserException("No in-range spectrum to write");
            }

            var hasPeaks = peaksIndex.Count > 0;
            var dataBytes = BuildPointFacet(dataIndex, dataMz, dataIntensity, (int)ordinal);
            var metaBytes = BuildMetadataFacet(records);
            var peaksBytes = hasPeaks
                ? BuildPointFacet(peaksIndex, peaksMz, peaksIntensity, peakSpectra.Count)
                : null;
            var indexBytes = BuildIndex(hasPeaks);

            ConfigureWriter(".mzpeak");
            try
            {
                using (var zip = new ZipArchive(Writer.BaseStream, ZipArchiveMode.Create, true))
                {
                    AddStored(zip, "mzpeak_index.json", indexBytes);
                    AddStored(zip, "spectra_data.parquet", dataBytes);
                    AddStored(zip, "spectra_metadata.parquet", metaBytes);
                    if (hasPeaks) AddStored(zip, "spectra_peaks.parquet", peaksBytes);
                }

                Writer.Flush();
            }
            finally
            {
                Writer.Close();
            }

            Log.Info($"Wrote mzPeak archive with {ordinal} spectra ({dataIndex.Count} data points, {peaksIndex.Count} peak points)");
        }

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
                    Accession = "MS:1000045",
                    Name = "collision energy",
                    Float = reaction.CollisionEnergy,
                    Unit = "UO:0000266"
                });
            }

            int? charge = trailer.AsPositiveInt("Charge State:");
            double? monoisotopicMz = trailer.AsDouble("Monoisotopic M/Z:");
            rec.SelectedIonMz = CalculateSelectedIonMz(reaction, monoisotopicMz, isolationWidth);
            rec.ChargeState = charge;

            if (rec.SelectedIonMz > ZeroDelta && parentScan > 0)
            {
                rec.SelectedIonIntensity = (float)CalculatePrecursorPeakIntensity(raw, parentScan,
                    reaction.PrecursorMass, isolationWidth,
                    ParseInput.NoPeakPicking.Contains(rec.MsLevel - 1));
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

        private static byte[] BuildPointFacet(List<ulong> index, List<double> mz, List<float> intensity, int spectrumCount)
        {
            var point = new StructField("point",
                new DataField<ulong>("spectrum_index"),
                new DataField<double>("mz"),
                new DataField<float>("intensity"));
            var schema = new ParquetSchema(point);

            var cols = new Dictionary<DataField, (Array, int[], int[])>
            {
                [Leaf(schema, "point/spectrum_index")] = (index.ToArray(), null, null),
                [Leaf(schema, "point/mz")] = (mz.ToArray(), null, null),
                [Leaf(schema, "point/intensity")] = (intensity.ToArray(), null, null)
            };

            var meta = new Dictionary<string, string>
            {
                ["spectrum_count"] = spectrumCount.ToString(),
                ["spectrum_data_point_count"] = index.Count.ToString(),
                ["spectrum_array_index"] = SpectrumArrayIndex
            };

            return WriteFacet(schema, meta, cols);
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
            AddScalar(cols, schema, "spectrum/" + Cv("MS:1000511", "ms_level"),
                records.Select(r => (byte)r.MsLevel).ToArray(), presentAll);
            AddScalar(cols, schema, "spectrum/time", records.Select(r => r.Time).ToArray(), presentAll);
            AddScalar(cols, schema, "spectrum/" + Cv("MS:1000465", "scan_polarity"),
                records.Select(r => r.Polarity).ToArray(), presentAll);
            AddScalar(cols, schema, "spectrum/" + Cv("MS:1000525", "spectrum_representation"),
                records.Select(r => r.Representation).ToArray(), presentAll);
            AddScalar(cols, schema, "spectrum/" + Cv("MS:1000559", "spectrum_type"),
                records.Select(r => r.SpectrumType).ToArray(), presentAll);
            AddScalar(cols, schema, "spectrum/" + Cv("MS:1000528", "lowest_observed_mz", "MS:1000040"),
                records.Select(r => r.LowestMz).ToArray(), presentAll);
            AddScalar(cols, schema, "spectrum/" + Cv("MS:1000527", "highest_observed_mz", "MS:1000040"),
                records.Select(r => r.HighestMz).ToArray(), presentAll);
            AddScalar(cols, schema, "spectrum/" + Cv("MS:1003060", "number_of_data_points"),
                records.Select(r => r.DataPointCount).ToArray(), presentAll);

            // number_of_peaks: present only where peaks were written (leaf-null otherwise).
            var npkLeaf = Leaf(schema, "spectrum/" + Cv("MS:1003059", "number_of_peaks"));
            var npkRows = records.Select(r => r.PeakCount.HasValue
                ? MzPeakParquet.Present(npkLeaf)
                : MzPeakParquet.AtLevel(npkLeaf.MaxDefinitionLevel - 1, false)).ToArray();
            var (npkDef, _) = MzPeakParquet.NestedLevels(npkLeaf, npkRows);
            cols[npkLeaf] = (records.Where(r => r.PeakCount.HasValue).Select(r => r.PeakCount.Value).ToArray(), npkDef, null);

            AddScalar(cols, schema, "spectrum/" + Cv("MS:1000504", "base_peak_mz", "MS:1000040"),
                records.Select(r => r.BasePeakMz).ToArray(), presentAll);
            AddScalar(cols, schema, "spectrum/" + Cv("MS:1000505", "base_peak_intensity", "MS:1000131"),
                records.Select(r => r.BasePeakIntensity).ToArray(), presentAll);
            AddScalar(cols, schema, "spectrum/" + Cv("MS:1000285", "total_ion_current", "MS:1000131"),
                records.Select(r => r.TotalIonCurrent).ToArray(), presentAll);

            AddParamList(cols, schema, "spectrum/parameters", records.Select(r => r.SpectrumParams).ToList(), presentAll);

            // scan facet (present on all N rows)
            AddScalar(cols, schema, "scan/source_index", records.Select(r => r.Ordinal).ToArray(), presentAll);
            AddScalar(cols, schema, "scan/scan_index", records.Select(r => r.Ordinal).ToArray(), presentAll);
            AddScalar(cols, schema, "scan/" + Cv("MS:1000016", "scan_start_time", "UO:0000031"),
                records.Select(r => r.ScanStartTime).ToArray(), presentAll);
            AddScalar(cols, schema, "scan/" + Cv("MS:1000512", "filter_string"),
                records.Select(r => r.FilterString).ToArray(), presentAll);
            AddScalar(cols, schema, "scan/" + Cv("MS:1000927", "ion_injection_time", "UO:0000028"),
                records.Select(r => r.IonInjectionTime).ToArray(), presentAll);
            AddScalar(cols, schema, "scan/instrument_configuration_ref",
                records.Select(r => r.InstrumentConfigRef).ToArray(), presentAll);
            AddScanWindows(cols, schema, records);

            // precursor facet (present on rows 0..M-1, null on M..N-1)
            AddMsnScalar(cols, schema, "precursor/source_index", n, msnRecords.Select(r => r.Ordinal).ToArray());
            AddMsnPrecursorIndex(cols, schema, "precursor/precursor_index", n, msnRecords);
            AddMsnString(cols, schema, "precursor/precursor_id", n, msnRecords.Select(r => r.PrecursorId).ToArray());
            AddMsnScalar(cols, schema, "precursor/isolation_window/" + Cv("MS:1000827", "isolation_window_target_mz"),
                n, msnRecords.Select(r => r.IsolationTarget).ToArray());
            AddMsnNullableFloat(cols, schema, "precursor/isolation_window/" + Cv("MS:1000828", "isolation_window_lower_offset", "MS:1000040"),
                n, msnRecords.Select(r => r.IsolationLowerOffset).ToArray());
            AddMsnNullableFloat(cols, schema, "precursor/isolation_window/" + Cv("MS:1000829", "isolation_window_upper_offset", "MS:1000040"),
                n, msnRecords.Select(r => r.IsolationUpperOffset).ToArray());
            AddMsnParamList(cols, schema, "precursor/isolation_window/parameters", n, msnRecords.Select(_ => new List<Param>()).ToList());
            AddMsnParamList(cols, schema, "precursor/activation/parameters", n, msnRecords.Select(r => r.ActivationParams).ToList());

            // selected_ion facet (present on rows 0..M-1, null on M..N-1)
            AddMsnScalar(cols, schema, "selected_ion/source_index", n, msnRecords.Select(r => r.Ordinal).ToArray());
            AddMsnPrecursorIndex(cols, schema, "selected_ion/precursor_index", n, msnRecords);
            AddMsnScalar(cols, schema, "selected_ion/" + Cv("MS:1000744", "selected_ion_mz", "MS:1000040"),
                n, msnRecords.Select(r => r.SelectedIonMz).ToArray());
            AddMsnNullableInt(cols, schema, "selected_ion/" + Cv("MS:1000041", "charge_state"),
                n, msnRecords.Select(r => r.ChargeState).ToArray());
            AddMsnNullableFloat(cols, schema, "selected_ion/" + Cv("MS:1000042", "intensity", "MS:1000131"),
                n, msnRecords.Select(r => r.SelectedIonIntensity).ToArray());

            var custom = new Dictionary<string, string>
            {
                ["spectrum_count"] = n.ToString(),
                ["spectrum_data_point_count"] = "0",
                ["spectrum_array_index"] = SpectrumArrayIndex
            };
            AddMetadataBlocks(custom, records);

            return WriteFacet(schema, custom, cols);
        }

        private StructField BuildSpectrumField()
        {
            return new StructField("spectrum",
                new DataField<ulong>("index", true),
                new DataField<string>("id", true),
                new DataField<byte>(Cv("MS:1000511", "ms_level"), true),
                new DataField<double>("time", true),
                new DataField<sbyte>(Cv("MS:1000465", "scan_polarity"), true),
                new DataField<string>(Cv("MS:1000525", "spectrum_representation"), true),
                new DataField<string>(Cv("MS:1000559", "spectrum_type"), true),
                new DataField<double>(Cv("MS:1000528", "lowest_observed_mz", "MS:1000040"), true),
                new DataField<double>(Cv("MS:1000527", "highest_observed_mz", "MS:1000040"), true),
                new DataField<ulong>(Cv("MS:1003060", "number_of_data_points"), true),
                new DataField<ulong>(Cv("MS:1003059", "number_of_peaks"), true),
                new DataField<double>(Cv("MS:1000504", "base_peak_mz", "MS:1000040"), true),
                new DataField<float>(Cv("MS:1000505", "base_peak_intensity", "MS:1000131"), true),
                new DataField<float>(Cv("MS:1000285", "total_ion_current", "MS:1000131"), true),
                new ListField("parameters", MzPeakParquet.BuildParamField("item")));
        }

        private StructField BuildScanField()
        {
            var window = new StructField("item",
                new DataField<float>(Cv("MS:1000501", "scan_window_lower_limit", "MS:1000040"), true),
                new DataField<float>(Cv("MS:1000500", "scan_window_upper_limit", "MS:1000040"), true));
            return new StructField("scan",
                new DataField<ulong>("source_index", true),
                new DataField<ulong>("scan_index", true),
                new DataField<float>(Cv("MS:1000016", "scan_start_time", "UO:0000031"), true),
                new DataField<string>(Cv("MS:1000512", "filter_string"), true),
                new DataField<float>(Cv("MS:1000927", "ion_injection_time", "UO:0000028"), true),
                new DataField<uint>("instrument_configuration_ref", true),
                new ListField("scan_windows", window));
        }

        private StructField BuildPrecursorField()
        {
            var isolationWindow = new StructField("isolation_window",
                new DataField<float>(Cv("MS:1000827", "isolation_window_target_mz"), true),
                new DataField<float>(Cv("MS:1000828", "isolation_window_lower_offset", "MS:1000040"), true),
                new DataField<float>(Cv("MS:1000829", "isolation_window_upper_offset", "MS:1000040"), true),
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
                new DataField<double>(Cv("MS:1000744", "selected_ion_mz", "MS:1000040"), true),
                new DataField<int>(Cv("MS:1000041", "charge_state"), true),
                new DataField<float>(Cv("MS:1000042", "intensity", "MS:1000131"), true));
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

        private static void AddMsnNullableFloat(IDictionary<DataField, (Array, int[], int[])> cols,
            ParquetSchema schema, string path, int n, float?[] values)
        {
            var leaf = Leaf(schema, path);
            int m = values.Length;
            var rows = new List<MzPeakParquet.LeafRow>();
            var present = new List<float>();
            for (int i = 0; i < n; i++)
            {
                if (i >= m) rows.Add(MzPeakParquet.Absent());
                else if (values[i].HasValue) { rows.Add(MzPeakParquet.Present(leaf)); present.Add(values[i].Value); }
                else rows.Add(MzPeakParquet.AtLevel(leaf.MaxDefinitionLevel - 1, false));
            }
            var (def, _) = MzPeakParquet.NestedLevels(leaf, rows);
            cols[leaf] = (present.ToArray(), def, null);
        }

        private static void AddMsnNullableInt(IDictionary<DataField, (Array, int[], int[])> cols,
            ParquetSchema schema, string path, int n, int?[] values)
        {
            var leaf = Leaf(schema, path);
            int m = values.Length;
            var rows = new List<MzPeakParquet.LeafRow>();
            var present = new List<int>();
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
            var lowerLeaf = Leaf(schema, "scan/scan_windows/list/item/" + Cv("MS:1000501", "scan_window_lower_limit", "MS:1000040"));
            var upperLeaf = Leaf(schema, "scan/scan_windows/list/item/" + Cv("MS:1000500", "scan_window_upper_limit", "MS:1000040"));

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
                    CvParam("MS:1000529", "instrument serial number", _instrumentSerial)
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
                    ["parameters"] = new JArray { CvParam("MS:1003145", "ThermoRawFileParser", null) }
                }
            };
        }

        private JArray BuildDataProcessingList()
        {
            return new JArray
            {
                new JObject
                {
                    ["id"] = "trfp_conversion",
                    ["methods"] = new JArray
                    {
                        new JObject
                        {
                            ["order"] = 0,
                            ["software_reference"] = "ThermoRawFileParser",
                            ["parameters"] = new JArray { CvParam("MS:1000544", "Conversion to mzML", null) }
                        },
                        new JObject
                        {
                            ["order"] = 1,
                            ["software_reference"] = "ThermoRawFileParser",
                            ["parameters"] = new JArray { JParam("intensity narrowing", null, "f64 to f32", null) }
                        }
                    }
                }
            };
        }

        private JObject BuildFileDescription(List<Record> records)
        {
            var contents = new JArray
            {
                CvParam("MS:1000579", "MS1 spectrum", null),
                CvParam("MS:1000580", "MSn spectrum", null)
            };
            var sourceParams = new JArray
            {
                CvParam("MS:1000768", "Thermo nativeID format", null),
                CvParam("MS:1000563", "Thermo RAW format", null)
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

        private byte[] BuildIndex(bool hasPeaks)
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
