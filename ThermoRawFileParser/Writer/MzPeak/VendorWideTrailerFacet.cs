using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Apache.Arrow;
using Apache.Arrow.Types;
using ThermoFisher.CommonCore.Data.Interfaces;

namespace ThermoRawFileParser.Writer
{
    // Streamed vendor_scan_trailers_wide facet: one row per committed spectrum, one TYPED column per
    // trailer-header label (numeric labels -> nullable double; others -> verbatim string), classified
    // once from the run's stable trailer header. Column names are sanitized; the exact label -> column
    // mapping (+ value_kind) is recorded in vendor_trailer_schema. Uses ParquetSharp/Arrow.
    internal static class VendorWideTrailerFacet
    {
        private static readonly HashSet<string> NumericTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "SHORT", "USHORT", "INT", "UINT", "LONG", "ULONG", "FLOAT", "DOUBLE", "SBYTE", "BYTE", "CHAR" };

        internal sealed class Column { public string Label; public string DataType; public string Name; public bool Numeric; }

        internal static List<Column> Classify(IRawDataPlus raw)
        {
            var cols = new List<Column>();
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var header = raw.GetTrailerExtraHeaderInformation();
                for (int i = 0; i < header.Length; i++)
                {
                    var dt = header[i].DataType.ToString();
                    var name = Unique(Sanitize(header[i].Label, i), used);
                    cols.Add(new Column { Label = header[i].Label ?? "", DataType = dt, Name = name, Numeric = NumericTypes.Contains(dt) });
                }
            }
            catch { }
            return cols;
        }

        internal static long Write(IRawDataPlus raw, IReadOnlyList<(ulong ordinal, int scan)> committed,
            List<Column> cols, string tempPath)
        {
            var schemaBuilder = new Schema.Builder()
                .Field(new Field("ordinal",     new UInt64Type(), nullable: false))
                .Field(new Field("scan_number", new Int32Type(),  nullable: false));
            foreach (var c in cols)
                schemaBuilder.Field(new Field(c.Name,
                    c.Numeric ? (IArrowType)new DoubleType() : new StringType(),
                    nullable: true));
            var schema = schemaBuilder.Build();

            const int flushRows = 50_000;
            var ord  = new List<ulong>();
            var scan = new List<int>();
            var dbl  = cols.Select(_ => new List<double?>()).ToArray();
            var str  = cols.Select(_ => new List<string>()).ToArray();
            long rows = 0;

            using var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write);
            var managedSink = new ParquetSharp.IO.ManagedOutputStream(fs);
            var writerProps = new ParquetSharp.WriterPropertiesBuilder()
                .Compression(ParquetSharp.Compression.Zstd)
                .Build();
            var arrowProps = new ParquetSharp.Arrow.ArrowWriterPropertiesBuilder()
                .StoreSchema()
                .Build();
            var writer = new ParquetSharp.Arrow.FileWriter(managedSink, schema, writerProps, arrowProps);

            void Flush()
            {
                if (ord.Count == 0) return;

                var arrays = new List<IArrowArray>();
                var ordB = new UInt64Array.Builder(); foreach (var v in ord)  ordB.Append(v);
                arrays.Add(ordB.Build());
                var scB  = new Int32Array.Builder();  foreach (var v in scan) scB.Append(v);
                arrays.Add(scB.Build());

                for (int c = 0; c < cols.Count; c++)
                {
                    if (cols[c].Numeric)
                    {
                        var b = new DoubleArray.Builder();
                        foreach (var v in dbl[c]) { if (v.HasValue) b.Append(v.Value); else b.AppendNull(); }
                        arrays.Add(b.Build());
                    }
                    else
                    {
                        var b = new StringArray.Builder();
                        foreach (var v in str[c]) { if (v != null) b.Append(v); else b.AppendNull(); }
                        arrays.Add(b.Build());
                    }
                }

                writer.WriteRecordBatch(new RecordBatch(schema, arrays.ToArray(), ord.Count));
                ord.Clear(); scan.Clear();
                for (int c = 0; c < cols.Count; c++) { dbl[c].Clear(); str[c].Clear(); }
            }

            foreach (var (ordinal, scanNumber) in committed)
            {
                var info = raw.GetTrailerExtraInformation(scanNumber);
                object[] typed = null;
                try { typed = raw.GetTrailerExtraValues(scanNumber); } catch { }
                ord.Add(ordinal); scan.Add(scanNumber);
                for (int c = 0; c < cols.Count; c++)
                {
                    if (cols[c].Numeric)
                        dbl[c].Add(MzPeakSpectrumWriter.NumericOrNull(typed != null && c < typed.Length ? typed[c] : null));
                    else
                        // info may be null for some scans (parity with the tall path's guard); keep
                        // the row aligned by emitting an empty string rather than dereferencing null.
                        str[c].Add(info != null && c < info.Length ? (info.Values[c] ?? "") : "");
                }
                rows++;
                if (ord.Count >= flushRows) Flush();
            }
            Flush();
            writer.Close();
            managedSink.Dispose();
            return rows;
        }

        private static string Sanitize(string label, int ordinal)
        {
            if (string.IsNullOrWhiteSpace(label)) return "col_" + ordinal;
            var sb = new StringBuilder(label.Length);
            bool lastUnderscore = false;
            foreach (var ch in label.Trim().ToLowerInvariant())
            {
                if ((ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9')) { sb.Append(ch); lastUnderscore = false; }
                else if (!lastUnderscore) { sb.Append('_'); lastUnderscore = true; }
            }
            var s = sb.ToString().Trim('_');
            return s.Length == 0 ? "col_" + ordinal : s;
        }

        private static string Unique(string name, HashSet<string> used)
        {
            if (used.Add(name)) return name;
            for (int k = 2; ; k++) { var c = name + "_" + k; if (used.Add(c)) return c; }
        }
    }
}
