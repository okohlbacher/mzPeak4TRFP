using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Parquet;
using Parquet.Data;
using Parquet.Schema;
using ThermoFisher.CommonCore.Data.Interfaces;

namespace ThermoRawFileParser.Writer
{
    // Streamed vendor_scan_trailers_wide facet: one row per committed spectrum, one TYPED column per
    // trailer-header label (numeric labels -> nullable double; others -> verbatim string), classified
    // once from the run's stable trailer header. Column names are sanitized; the exact label -> column
    // mapping (+ value_kind) is recorded in vendor_trailer_schema. This is the analytics-friendly
    // counterpart to the tall vendor_scan_trailers (a pivot of the same data); opt-in via
    // --vendor-metadata=wide|both.
    internal static class VendorWideTrailerFacet
    {
        // Thermo GenericDataTypes whose typed value is a number (rendered as a double column).
        private static readonly HashSet<string> NumericTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "SHORT", "USHORT", "INT", "UINT", "LONG", "ULONG", "FLOAT", "DOUBLE", "SBYTE", "BYTE", "CHAR" };

        // Header classification result, reused for the schema sidecar.
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
            catch { /* header unavailable: empty classification */ }
            return cols;
        }

        // Writes the wide facet over the committed scans (ordinal order). Returns the row count.
        internal static long Write(IRawDataPlus raw, IReadOnlyList<(ulong ordinal, int scan)> committed,
            List<Column> cols, string tempPath)
        {
            var fields = new List<Field> { new DataField<ulong>("ordinal"), new DataField<int>("scan_number") };
            foreach (var c in cols)
                fields.Add(c.Numeric ? (DataField)new DataField<double?>(c.Name) : new DataField<string>(c.Name));
            var schema = new ParquetSchema(fields);

            const int flushRows = 50_000;
            var ord = new List<ulong>(); var scan = new List<int>();
            var dbl = cols.Select(_ => new List<double?>()).ToArray();
            var str = cols.Select(_ => new List<string>()).ToArray();
            long rows = 0;

            using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write))
            using (var w = ParquetWriter.CreateAsync(schema, fs).GetAwaiter().GetResult())
            {
                void Flush()
                {
                    if (ord.Count == 0) return;
                    using (var rg = w.CreateRowGroup())
                    {
                        rg.WriteColumnAsync(new DataColumn((DataField)schema[0], ord.ToArray())).GetAwaiter().GetResult();
                        rg.WriteColumnAsync(new DataColumn((DataField)schema[1], scan.ToArray())).GetAwaiter().GetResult();
                        for (int c = 0; c < cols.Count; c++)
                            rg.WriteColumnAsync(new DataColumn((DataField)schema[c + 2],
                                cols[c].Numeric ? (Array)dbl[c].ToArray() : str[c].ToArray())).GetAwaiter().GetResult();
                    }
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
                            str[c].Add(c < info.Length ? (info.Values[c] ?? "") : "");
                    }
                    rows++;
                    if (ord.Count >= flushRows) Flush();
                }
                Flush();
            }
            return rows;
        }

        // A Parquet-safe column name from a trailer label: lower-case, non-alphanumeric runs -> '_'.
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
