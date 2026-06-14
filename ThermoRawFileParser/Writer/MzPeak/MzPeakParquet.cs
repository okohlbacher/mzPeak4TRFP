using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Parquet;
using Parquet.Data;
using Parquet.Schema;

namespace ThermoRawFileParser.Writer
{
    public struct MzPeakParam
    {
        public long? Integer;
        public double? Float;
        public string String;
        public bool? Boolean;
        public string Accession;
        public string Name;
        public string Unit;
    }

    public static class MzPeakParquet
    {
        public static StructField BuildParamField(string name)
        {
            var value = new StructField("value",
                new DataField<long>("integer", true),
                new DataField<double>("float", true),
                new DataField<string>("string", true),
                new DataField<bool>("boolean", true));

            return new StructField(name,
                value,
                new DataField<string>("accession", true),
                new DataField<string>("name", true),
                new DataField<string>("unit", true));
        }

        // The label must already be normalized to snake_case by the caller; this method
        // performs no label normalization and only rewrites the ':' in CURIE accessions to '_'.
        public static string CvColumn(string accession, string label, string unitAccession = null)
        {
            var head = accession.Replace(':', '_') + "_" + label;
            if (unitAccession == null) return head;
            return head + "_unit_" + unitAccession.Replace(':', '_');
        }

        public static DataColumn Column(DataField field, Array defined, int[] defLevels, int[] repLevels)
        {
            if (defLevels == null) return new DataColumn(field, defined);
            return new DataColumn(field, defined, defLevels, repLevels);
        }

        public static async Task WriteAsync(Stream output, ParquetSchema schema,
            IReadOnlyDictionary<string, string> customMetadata,
            IDictionary<DataField, (Array defined, int[] defLevels, int[] repLevels)> columns)
        {
            using (var writer = await ParquetWriter.CreateAsync(schema, output))
            {
                writer.CompressionMethod = CompressionMethod.Zstd;
                if (customMetadata != null) writer.CustomMetadata = customMetadata;

                using (var rg = writer.CreateRowGroup())
                {
                    foreach (var field in schema.GetDataFields())
                    {
                        var triple = columns[field];
                        await rg.WriteColumnAsync(Column(field, triple.defined, triple.defLevels, triple.repLevels));
                    }
                }
            }
        }
    }
}
