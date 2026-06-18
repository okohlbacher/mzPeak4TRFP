using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Apache.Arrow;
using Apache.Arrow.Types;
using log4net;
using ThermoFisher.CommonCore.Data.Interfaces;

namespace ThermoRawFileParser.Writer
{
    // Streamed vendor_scan_trailers_wide facet: one row per committed spectrum, one TYPED column per
    // trailer-header label (numeric labels -> nullable double; others -> verbatim string), classified
    // once from the run's stable trailer header. Column names are sanitized; the exact label -> column
    // mapping (+ value_kind) is recorded in vendor_trailer_schema. Uses ParquetSharp/Arrow.
    internal static class VendorWideTrailerFacet
    {
        private static readonly ILog Log =
            LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        // CHAR is excluded: a character trailer value coerced to double is meaningless — keep it a string.
        private static readonly HashSet<string> NumericTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "SHORT", "USHORT", "INT", "UINT", "LONG", "ULONG", "FLOAT", "DOUBLE", "SBYTE", "BYTE" };

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
            catch (Exception ex) { Log.Warn($"vendor_scan_trailers_wide: trailer-header classification failed, columns may be incomplete ({ex.Message})"); }
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
            ParquetSharp.IO.ManagedOutputStream managedSink = null;
            ParquetSharp.Arrow.FileWriter writer = null;
            bool closed = false;

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

            try
            {
                managedSink = new ParquetSharp.IO.ManagedOutputStream(fs);
                writer = VendorArrow.OpenWriter(managedSink, schema);

                foreach (var (ordinal, scanNumber) in committed)
                {
                    // Best-effort per scan: a trailer-read failure logs and leaves that row's values empty,
                    // rather than aborting the (opt-in) wide facet or the run.
                    ILogEntryAccess info = null;
                    object[] typed = null;
                    try { info = raw.GetTrailerExtraInformation(scanNumber); typed = raw.GetTrailerExtraValues(scanNumber); }
                    catch (Exception ex) { Log.Warn($"vendor_scan_trailers_wide: scan #{scanNumber} trailer read failed ({ex.Message})"); }
                    ord.Add(ordinal); scan.Add(scanNumber);
                    for (int c = 0; c < cols.Count; c++)
                    {
                        if (cols[c].Numeric)
                            dbl[c].Add(MzPeakSpectrumWriter.NumericOrNull(typed != null && c < typed.Length ? typed[c] : null));
                        else
                            str[c].Add(info != null && c < info.Length ? (info.Values[c] ?? "") : "");
                    }
                    rows++;
                    if (ord.Count >= flushRows) Flush();
                }
                Flush();
                writer.Close();   // in try: a footer-write failure must propagate, not yield a corrupt facet
                closed = true;
            }
            finally
            {
                if (writer != null && !closed) { try { writer.Close(); } catch { } }
                managedSink?.Dispose();
            }
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
