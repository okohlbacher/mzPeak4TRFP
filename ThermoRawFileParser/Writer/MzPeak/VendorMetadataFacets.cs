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
        // vendor_scan_trailers (tall): one row per (scan, trailer label), streamed to a temp file.
        internal static long WriteVendorScanTrailers(IRawDataPlus raw, int first, int last, string tempPath)
        {
            var schema = new ParquetSchema(
                new DataField<ulong>("scan_index"), new DataField<string>("label"),
                new DataField<string>("value"), new DataField<double?>("value_float"));

            using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write))
            using (var w = ParquetWriter.CreateAsync(schema, fs).GetAwaiter().GetResult())
            {
                const int flushRows = 1_000_000;
                var si = new List<ulong>(); var lab = new List<string>(); var val = new List<string>();
                long total = 0;

                void Flush()
                {
                    if (si.Count == 0) return;
                    using (var rg = w.CreateRowGroup())
                    {
                        rg.WriteColumnAsync(new DataColumn((DataField)schema[0], si.ToArray())).GetAwaiter().GetResult();
                        rg.WriteColumnAsync(new DataColumn((DataField)schema[1], lab.ToArray())).GetAwaiter().GetResult();
                        rg.WriteColumnAsync(new DataColumn((DataField)schema[2], val.ToArray())).GetAwaiter().GetResult();
                        rg.WriteColumnAsync(new DataColumn((DataField)schema[3], val.Select(ParseVendorFloat).ToArray())).GetAwaiter().GetResult();
                    }
                    si.Clear(); lab.Clear(); val.Clear();
                }

                for (int s = first; s <= last; s++)
                {
                    ILogEntryAccess t;
                    try { t = raw.GetTrailerExtraInformation(s); } catch { continue; }
                    if (t == null) continue;
                    for (int i = 0; i < t.Length; i++)
                    { si.Add((ulong)s); lab.Add(t.Labels[i] ?? ""); val.Add(t.Values[i] ?? ""); total++; }
                    if (si.Count >= flushRows) Flush();
                }
                Flush();
                return total;
            }
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
        internal static byte[] BuildVendorTrailerSchema(IRawDataPlus raw)
        {
            var ord = new List<int>(); var lab = new List<string>(); var dtype = new List<string>();
            try
            {
                var header = raw.GetTrailerExtraHeaderInformation();
                for (int i = 0; i < header.Length; i++)
                { ord.Add(i); lab.Add(header[i].Label ?? ""); dtype.Add(header[i].DataType.ToString()); }
            }
            catch { /* header unavailable: emit empty schema facet */ }

            var schema = new ParquetSchema(
                new DataField<int>("ordinal"), new DataField<string>("label"), new DataField<string>("data_type"));
            var cols = new (DataField, Array)[]
            {
                ((DataField)schema[0], ord.ToArray()), ((DataField)schema[1], lab.ToArray()),
                ((DataField)schema[2], dtype.ToArray())
            };
            return WriteFlatFacet(schema, cols);
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
