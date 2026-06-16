using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Parquet;
using Parquet.Data;
using Parquet.Schema;
using ThermoFisher.CommonCore.Data.Interfaces;

namespace ThermoRawFileParser.Writer
{
    // Verbatim Thermo vendor-metadata facets (opt-in, --vendor-metadata). Captures the metadata that
    // mzML's CV vocabulary cannot represent — the per-scan Trailer Extra bag, tune/sample/run-header/
    // method, and the trailer schema — exactly as the instrument reports it. These are additive,
    // non-CV facets: every value is the source string (value_float is a best-effort numeric parse so
    // the table is directly queryable). Tall layout keeps the schema stable across instruments/methods
    // and preserves the exact labels; vendor_trailer_schema lets a consumer pivot to a typed wide view.
    public partial class MzPeakSpectrumWriter
    {
        // A typed trailer/status value (GetTrailerExtraValues / IStatusLogEntry.Values) as a double, or
        // null when non-numeric (bool/string/section-header) — avoids culture-dependent string parsing.
        internal static double? NumericOrNull(object o)
        {
            if (o == null || o is bool || o is string) return null;
            try { return Convert.ToDouble(o, CultureInfo.InvariantCulture); } catch { return null; }
        }

        // vendor_status_log (tall): the per-RT status-log timeseries (sensor voltages/temps/pressures),
        // one row per (timepoint position, label). value is verbatim; value_float is the typed value.
        internal static byte[] BuildVendorStatusLog(IRawDataPlus raw)
        {
            var pos = new List<int>(); var rt = new List<double>();
            var lab = new List<string>(); var val = new List<string>(); var flt = new List<double?>();
            try
            {
                var header = raw.GetStatusLogHeaderInformation();
                int n = raw.GetStatusLogEntriesCount();
                for (int p = 0; p < n; p++)
                {
                    var e = raw.GetStatusLogEntry(p);
                    var vals = e.Values;
                    int m = Math.Min(header.Length, vals?.Length ?? 0);
                    for (int j = 0; j < m; j++)
                    {
                        pos.Add(p); rt.Add(e.Time);
                        lab.Add(header[j].Label ?? ""); val.Add(vals[j]?.ToString() ?? ""); flt.Add(NumericOrNull(vals[j]));
                    }
                }
            }
            catch { }

            var schema = new ParquetSchema(
                new DataField<int>("position"), new DataField<double>("rt"), new DataField<string>("label"),
                new DataField<string>("value"), new DataField<double?>("value_float"));
            return WriteFlatFacet(schema, new (DataField, Array)[]
            {
                ((DataField)schema[0], pos.ToArray()), ((DataField)schema[1], rt.ToArray()),
                ((DataField)schema[2], lab.ToArray()), ((DataField)schema[3], val.ToArray()),
                ((DataField)schema[4], flt.ToArray())
            });
        }

        // vendor_error_log: the instrument error log (usually empty), one row per entry.
        internal static byte[] BuildVendorErrorLog(IRawDataPlus raw)
        {
            var idx = new List<int>(); var rt = new List<double?>(); var msg = new List<string>();
            try
            {
                int n = raw.RunHeaderEx.ErrorLogCount;
                for (int i = 0; i < n; i++)
                {
                    var item = raw.GetErrorLogItem(i);
                    if (item == null) continue;
                    var mp = item.GetType().GetProperty("Message");
                    var tp = item.GetType().GetProperty("RetentionTime") ?? item.GetType().GetProperty("Time");
                    idx.Add(i);
                    rt.Add(tp != null ? NumericOrNull(Try(() => tp.GetValue(item))) : null);
                    msg.Add((mp != null ? Try(() => mp.GetValue(item))?.ToString() : item.ToString()) ?? "");
                }
            }
            catch { }

            var schema = new ParquetSchema(
                new DataField<int>("index"), new DataField<double?>("rt"), new DataField<string>("message"));
            return WriteFlatFacet(schema, new (DataField, Array)[]
            {
                ((DataField)schema[0], idx.ToArray()), ((DataField)schema[1], rt.ToArray()), ((DataField)schema[2], msg.ToArray())
            });
        }

        // vendor_file_metadata (tall): file-level metadata tagged by category.
        internal static byte[] BuildVendorFileMetadata(IRawDataPlus raw)
        {
            var cat = new List<string>(); var idx = new List<int>();
            var lab = new List<string>(); var val = new List<string>();

            void Add(string c, int i, string l, string v) { cat.Add(c); idx.Add(i); lab.Add(l ?? ""); val.Add(v ?? ""); }
            void AddLog(string c, ILogEntryAccess log) { if (log == null) return; for (int i = 0; i < log.Length; i++) Add(c, i, log.Labels[i], log.Values[i]); }
            void AddProps(string c, object o)
            {
                if (o == null) return; int i = 0;
                foreach (var p in o.GetType().GetProperties().Where(p => p.GetIndexParameters().Length == 0).OrderBy(p => p.Name))
                {
                    object v; try { v = p.GetValue(o); } catch { continue; }
                    if (v is IEnumerable && !(v is string)) continue;
                    Add(c, i++, p.Name, v?.ToString());
                }
            }

            AddProps("instrument", Try(() => (object)raw.GetInstrumentData()));
            AddProps("sample", Try(() => (object)raw.SampleInformation));
            AddProps("run_header", Try(() => (object)raw.RunHeaderEx));
            int tunes = Try(() => raw.GetTuneDataCount());
            for (int t = 0; t < tunes; t++) AddLog($"tune[{t}]", Try(() => raw.GetTuneData(t)));
            AddLog("status_log_header", Try(() => raw.GetStatusLogForRetentionTime(raw.RunHeaderEx.StartTime)));
            try { for (int m = 0; m < raw.InstrumentMethodsCount; m++) Add("instrument_method", m, $"method[{m}]", raw.GetInstrumentMethod(m)); } catch { }

            var schema = new ParquetSchema(
                new DataField<string>("category"), new DataField<int>("entry_index"),
                new DataField<string>("label"), new DataField<string>("value"), new DataField<double?>("value_float"));
            var cols = new (DataField, Array)[]
            {
                ((DataField)schema[0], cat.ToArray()), ((DataField)schema[1], idx.ToArray()),
                ((DataField)schema[2], lab.ToArray()), ((DataField)schema[3], val.ToArray()),
                ((DataField)schema[4], val.Select(ParseVendorFloat).ToArray())
            };
            return WriteFlatFacet(schema, cols);
        }

        // vendor_trailer_schema: the trailer header (label, data_type) — lets a consumer pivot the tall
        // vendor_scan_trailers into a typed wide view deterministically.
        // The trailer header as a sidecar: ordinal, exact label, Thermo data_type, the sanitized wide
        // column_name, and value_kind (numeric/string) — lets a consumer pivot the tall trailers into a
        // typed wide view and maps verbatim labels to the wide facet's columns.
        internal static byte[] BuildVendorTrailerSchema(List<VendorWideTrailerFacet.Column> cols)
        {
            var ord = new List<int>(); var lab = new List<string>(); var dtype = new List<string>();
            var name = new List<string>(); var kind = new List<string>();
            for (int i = 0; i < cols.Count; i++)
            {
                ord.Add(i); lab.Add(cols[i].Label); dtype.Add(cols[i].DataType);
                name.Add(cols[i].Name); kind.Add(cols[i].Numeric ? "numeric" : "string");
            }

            var schema = new ParquetSchema(
                new DataField<int>("ordinal"), new DataField<string>("label"), new DataField<string>("data_type"),
                new DataField<string>("column_name"), new DataField<string>("value_kind"));
            return WriteFlatFacet(schema, new (DataField, Array)[]
            {
                ((DataField)schema[0], ord.ToArray()), ((DataField)schema[1], lab.ToArray()),
                ((DataField)schema[2], dtype.ToArray()), ((DataField)schema[3], name.ToArray()),
                ((DataField)schema[4], kind.ToArray())
            });
        }

        // Readable JSON sidecar of the FILE-LEVEL vendor metadata (instrument/sample/run-header/tune/
        // status-log header/method/trailer schema). Per-scan trailers are NOT included — at 85 fields ×
        // hundreds of thousands of scans they belong in the vendor_scan_trailers parquet facet, not JSON.
        internal static string BuildVendorMetadataJson(IRawDataPlus raw, string sourceName)
        {
            JObject Props(object o)
            {
                var j = new JObject();
                if (o == null) return j;
                foreach (var p in o.GetType().GetProperties().Where(p => p.GetIndexParameters().Length == 0).OrderBy(p => p.Name))
                {
                    object v; try { v = p.GetValue(o); } catch { continue; }
                    if (v is IEnumerable && !(v is string)) continue;
                    j[p.Name] = v?.ToString();
                }
                return j;
            }
            JArray Entries(ILogEntryAccess log)
            {
                var a = new JArray();
                if (log == null) return a;
                for (int i = 0; i < log.Length; i++)
                    a.Add(new JObject { ["label"] = log.Labels[i], ["value"] = log.Values[i] });
                return a;
            }

            var tune = new JArray();
            for (int t = 0; t < Try(() => raw.GetTuneDataCount()); t++)
                tune.Add(new JObject { ["segment"] = t, ["entries"] = Entries(Try(() => raw.GetTuneData(t))) });

            var methods = new JArray();
            try { for (int m = 0; m < raw.InstrumentMethodsCount; m++) methods.Add(raw.GetInstrumentMethod(m)); } catch { }

            var schema = new JArray();
            try
            {
                var h = raw.GetTrailerExtraHeaderInformation();
                for (int i = 0; i < h.Length; i++)
                    schema.Add(new JObject { ["ordinal"] = i, ["label"] = h[i].Label, ["data_type"] = h[i].DataType.ToString() });
            }
            catch { }

            var root = new JObject
            {
                ["source_file"] = sourceName,
                ["instrument"] = Props(Try(() => (object)raw.GetInstrumentData())),
                ["sample"] = Props(Try(() => (object)raw.SampleInformation)),
                ["run_header"] = Props(Try(() => (object)raw.RunHeaderEx)),
                ["tune"] = tune,
                ["status_log_header"] = Entries(Try(() => raw.GetStatusLogForRetentionTime(raw.RunHeaderEx.StartTime))),
                ["instrument_methods"] = methods,
                ["trailer_schema"] = schema
            };
            return root.ToString(Formatting.Indented);
        }

        private static byte[] WriteFlatFacet(ParquetSchema schema, (DataField field, Array values)[] cols)
        {
            using (var ms = new MemoryStream())
            {
                using (var w = ParquetWriter.CreateAsync(schema, ms).GetAwaiter().GetResult())
                using (var rg = w.CreateRowGroup())
                    foreach (var (field, values) in cols)
                        rg.WriteColumnAsync(new DataColumn(field, values)).GetAwaiter().GetResult();
                return ms.ToArray();
            }
        }

        private static double? ParseVendorFloat(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            if (double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var d)) return d;
            if (double.TryParse(s, NumberStyles.Any, CultureInfo.CurrentCulture, out d)) return d;
            return null;
        }

        private static T Try<T>(Func<T> f) { try { return f(); } catch { return default; } }
    }
}
