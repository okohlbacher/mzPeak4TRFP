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
using ThermoFisher.CommonCore.Data.Interfaces;
using ThermoRawFileParser.Util;

namespace ThermoRawFileParser.Writer
{
    public class MzPeakSpectrumWriter : SpectrumWriter
    {
        private static readonly ILog Log =
            LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private const string SpectrumArrayIndex =
            "{\"prefix\":\"point\",\"entries\":[" +
            "{\"context\":\"spectrum\",\"path\":\"point.mz\",\"data_type\":\"MS:1000523\",\"array_type\":\"MS:1000514\"," +
            "\"array_name\":\"m/z array\",\"unit\":\"MS:1000040\",\"buffer_format\":\"point\",\"buffer_priority\":\"primary\"}," +
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

            ConfigureWriter(".mzpeak");

            double[] masses = null;
            double[] intensities = null;
            double rt = 0;

            for (var scanNumber = firstScanNumber; scanNumber <= lastScanNumber && masses == null; scanNumber++)
            {
                try
                {
                    var scanFilter = raw.GetFilterForScanNumber(scanNumber);
                    int level = (int)scanFilter.MSOrder;
                    if (level > ParseInput.MaxLevel || !ParseInput.MsLevel.Contains(level)) continue;

                    var scanEvent = raw.GetScanEventForScanNumber(scanNumber);
                    var mzData = ReadMZData(raw, scanEvent, scanNumber,
                        !ParseInput.NoPeakPicking.Contains((int)scanFilter.MSOrder), false, false);

                    if (mzData.masses != null && mzData.masses.Length >= 1)
                    {
                        masses = mzData.masses;
                        intensities = mzData.intensities;
                        rt = raw.RetentionTimeFromScanNumber(scanNumber);
                    }
                }
                catch (Exception ex)
                {
                    Log.Error($"Scan #{scanNumber} cannot be processed because of the following exception: {ex.Message}");
                    Log.Debug($"{ex.StackTrace}\n{ex.InnerException}");
                    ParseInput.NewError();
                }
            }

            if (masses == null)
            {
                throw new RawFileParserException("No in-range spectrum with data points to write");
            }

            var dataBytes = BuildDataFacet(masses, intensities);
            var metaBytes = BuildMetadataFacet(rt);
            var indexBytes = BuildIndex();

            using (var zip = new ZipArchive(Writer.BaseStream, ZipArchiveMode.Create, true))
            {
                AddStored(zip, "mzpeak_index.json", indexBytes);
                AddStored(zip, "spectra_data.parquet", dataBytes);
                AddStored(zip, "spectra_metadata.parquet", metaBytes);
            }

            Writer.Flush();
            Writer.Close();

            Log.Info($"Wrote mzPeak archive with 1 spectrum ({masses.Length} points)");
        }

        private static void AddStored(ZipArchive zip, string name, byte[] bytes)
        {
            var entry = zip.CreateEntry(name, CompressionLevel.NoCompression);
            using (var s = entry.Open())
            {
                s.Write(bytes, 0, bytes.Length);
            }
        }

        private static byte[] BuildDataFacet(double[] masses, double[] intensities)
        {
            var point = new StructField("point",
                new DataField<ulong>("spectrum_index"),
                new DataField<double>("mz"),
                new DataField<float>("intensity"));
            var schema = new ParquetSchema(point);

            var n = masses.Length;
            var idx = new ulong[n];
            var mz = new double[n];
            var inten = new float[n];
            for (int i = 0; i < n; i++)
            {
                idx[i] = 0;
                mz[i] = masses[i];
                inten[i] = (float)intensities[i];
            }

            var cols = new Dictionary<DataField, (Array, int[], int[])>
            {
                [Leaf(schema, "point/spectrum_index")] = (idx, null, null),
                [Leaf(schema, "point/mz")] = (mz, null, null),
                [Leaf(schema, "point/intensity")] = (inten, null, null)
            };

            var meta = new Dictionary<string, string>
            {
                ["spectrum_count"] = "1",
                ["spectrum_data_point_count"] = n.ToString(),
                ["spectrum_array_index"] = SpectrumArrayIndex
            };

            return WriteFacet(schema, meta, cols);
        }

        private static byte[] BuildMetadataFacet(double rt)
        {
            var spectrum = new StructField("spectrum",
                new DataField<ulong>("index"),
                new DataField<string>("id", true),
                new DataField<double>("time", true));
            var schema = new ParquetSchema(spectrum);

            var cols = new Dictionary<DataField, (Array, int[], int[])>
            {
                [Leaf(schema, "spectrum/index")] = (new ulong[] { 0 }, new[] { 1 }, null),
                [Leaf(schema, "spectrum/id")] = (new[] { "index=0" }, new[] { 2 }, null),
                [Leaf(schema, "spectrum/time")] = (new[] { rt }, new[] { 2 }, null)
            };

            var meta = new Dictionary<string, string>
            {
                ["spectrum_count"] = "1",
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

        private static byte[] BuildIndex()
        {
            var index = new JObject
            {
                ["files"] = new JArray
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
                },
                ["metadata"] = new JObject()
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
