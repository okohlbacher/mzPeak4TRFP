using log4net;
using Newtonsoft.Json.Linq;
using Parquet.Data;
using Parquet.Schema;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using ThermoFisher.CommonCore.Data.Business;
using ThermoFisher.CommonCore.Data.FilterEnums;
using ThermoFisher.CommonCore.Data.Interfaces;
using ThermoRawFileParser.Util;

namespace ThermoRawFileParser.Writer
{
    public class MzPeakSpectrumWriter : SpectrumWriter
    {
        private static readonly ILog Log =
            LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private const string MzPeakVersion = "0.9";

        private const string SpectrumArrayIndex =
            "{\"prefix\":\"point\",\"entries\":[" +
            "{\"context\":\"spectrum\",\"path\":\"point.mz\",\"data_type\":\"MS:1000523\",\"array_type\":\"MS:1000514\"," +
            "\"array_name\":\"m/z array\",\"unit\":\"MS:1000040\",\"buffer_format\":\"point\",\"buffer_priority\":\"primary\",\"sorting_rank\":0}," +
            "{\"context\":\"spectrum\",\"path\":\"point.intensity\",\"data_type\":\"MS:1000521\",\"array_type\":\"MS:1000515\"," +
            "\"array_name\":\"intensity array\",\"unit\":\"MS:1000131\",\"buffer_format\":\"point\",\"buffer_priority\":\"primary\"}" +
            "]}";

        public MzPeakSpectrumWriter(ParseInput parseInput) : base(parseInput)
        {
        }

        public override void Write(IRawDataPlus raw, int firstScanNumber, int lastScanNumber)
        {
            if (!raw.HasMsData)
            {
                throw new RawFileParserException("No MS data in RAW file, no output will be produced");
            }

            var dataIndex = new List<ulong>();
            var dataMz = new List<double>();
            var dataIntensity = new List<float>();

            var metaIndex = new List<ulong>();
            var metaId = new List<string>();
            var metaTime = new List<double>();

            var peaksIndex = new List<ulong>();
            var peaksMz = new List<double>();
            var peaksIntensity = new List<float>();
            var peakSpectra = new HashSet<ulong>();

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
                        }
                    }

                    metaIndex.Add(ordinal);
                    metaId.Add($"index={ordinal}");
                    metaTime.Add(raw.RetentionTimeFromScanNumber(scanNumber));
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
            var metaBytes = BuildMetadataFacet(metaIndex, metaId, metaTime);
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

        private static byte[] BuildMetadataFacet(List<ulong> index, List<string> id, List<double> time)
        {
            var spectrum = new StructField("spectrum",
                new DataField<ulong>("index"),
                new DataField<string>("id", false),
                new DataField<double>("time", false));
            var schema = new ParquetSchema(spectrum);

            var n = index.Count;
            var def = new int[n];
            for (int i = 0; i < n; i++) def[i] = 1;

            var cols = new Dictionary<DataField, (Array, int[], int[])>
            {
                [Leaf(schema, "spectrum/index")] = (index.ToArray(), def, null),
                [Leaf(schema, "spectrum/id")] = (id.ToArray(), def, null),
                [Leaf(schema, "spectrum/time")] = (time.ToArray(), def, null)
            };

            var meta = new Dictionary<string, string>
            {
                ["spectrum_count"] = n.ToString(),
                ["spectrum_data_point_count"] = "0"
            };

            return WriteFacet(schema, meta, cols);
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

        private static byte[] BuildIndex(bool hasPeaks)
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

            var index = new JObject
            {
                ["version"] = MzPeakVersion,
                ["files"] = files,
                ["metadata"] = new JObject
                {
                    ["version"] = MzPeakVersion
                }
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
