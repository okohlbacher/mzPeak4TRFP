using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Apache.Arrow;
using Apache.Arrow.Types;
using MZPeak.Storage;
using MZPeak.Thermo;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ParquetSharp.Arrow;
using ThermoFisher.CommonCore.Data.Interfaces;

namespace ThermoRawFileParser.Writer
{
    // Verbatim Thermo vendor-metadata facets (opt-in, --vendor-metadata). Captures the metadata that
    // mzML's CV vocabulary cannot represent, written as additive non-CV proprietary entries into the
    // mzPeak archive. Uses ParquetSharp/Arrow for Parquet serialization.
    public partial class MzPeakSpectrumWriter
    {
        internal static double? NumericOrNull(object o)
        {
            if (o == null || o is bool || o is string) return null;
            try { return Convert.ToDouble(o, CultureInfo.InvariantCulture); } catch { return null; }
        }

        internal static void WriteVendorFacets(
            ThermoMZPeakWriter writer,
            IRawDataPlus raw,
            bool vendorTall,
            bool vendorWide,
            VendorTrailerFacetStream trailers,
            Dictionary<int, ulong> scanNumberToOrdinal,
            List<VendorWideTrailerFacet.Column> wideCols)
        {
            // Tall trailers were streamed to a temp file during the scan loop; copy them in verbatim.
            if (vendorTall && trailers != null)
            {
                var entry = new FileIndexEntry("vendor_scan_trailers.parquet",
                    new EntityType(EntityTypeTag.Other, "proprietary"), DataKind.Proprietary);
                using var dest = writer.StartProprietaryEntry(entry);
                using var src  = File.OpenRead(trailers.TempPath);
                src.CopyTo(dest, 65536);
            }

            if (vendorWide)
            {
                var committed = scanNumberToOrdinal
                    .OrderBy(kv => kv.Value)
                    .Select(kv => (kv.Value, kv.Key))
                    .ToList();
                string wideTemp = Path.GetTempFileName();
                try
                {
                    VendorWideTrailerFacet.Write(raw, committed, wideCols, wideTemp);
                    var entry = new FileIndexEntry("vendor_scan_trailers_wide.parquet",
                        new EntityType(EntityTypeTag.Other, "proprietary"), DataKind.Proprietary);
                    using var dest = writer.StartProprietaryEntry(entry);
                    using var src  = File.OpenRead(wideTemp);
                    src.CopyTo(dest, 65536);
                }
                finally { TryDelete(wideTemp); }
            }

            WriteVendorFileMetadata(writer, raw);
            WriteVendorTrailerSchema(writer, wideCols);
            WriteVendorStatusLog(writer, raw);
            WriteVendorErrorLog(writer, raw);
        }

        private static void WriteVendorStatusLog(ThermoMZPeakWriter writer, IRawDataPlus raw)
        {
            var pos = new List<int>(); var rt = new List<double>();
            var lab = new List<string>(); var val = new List<string>(); var flt = new List<double?>();
            try
            {
                var header = raw.GetStatusLogHeaderInformation();
                int n = raw.GetStatusLogEntriesCount();
                for (int p = 0; header != null && p < n; p++)
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
            catch (Exception ex) { Log.Warn($"vendor_status_log: partial/failed status-log read ({ex.Message})"); }

            var schema = VendorArrow.Schema(
                ("position",    new Int32Type(),  false),
                ("rt",          new DoubleType(), false),
                ("label",       new StringType(), false),
                ("value",       new StringType(), false),
                ("value_float", new DoubleType(), true));

            var batch = VendorArrow.Batch(schema,
                VendorArrow.Int32(pos), VendorArrow.Double(rt),
                VendorArrow.String(lab), VendorArrow.String(val),
                VendorArrow.NullableDouble(flt));

            WriteEntry(writer, "vendor_status_log.parquet", schema, batch);
        }

        private static void WriteVendorErrorLog(ThermoMZPeakWriter writer, IRawDataPlus raw)
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
            catch (Exception ex) { Log.Warn($"vendor_error_log: partial/failed error-log read ({ex.Message})"); }

            var schema = VendorArrow.Schema(
                ("index",   new Int32Type(),  false),
                ("rt",      new DoubleType(), true),
                ("message", new StringType(), false));

            var batch = VendorArrow.Batch(schema,
                VendorArrow.Int32(idx), VendorArrow.NullableDouble(rt), VendorArrow.String(msg));

            WriteEntry(writer, "vendor_error_log.parquet", schema, batch);
        }

        private static void WriteVendorFileMetadata(ThermoMZPeakWriter writer, IRawDataPlus raw)
        {
            var cat = new List<string>(); var idx = new List<int>();
            var lab = new List<string>(); var val = new List<string>();

            void Add(string c, int i, string l, string v) { cat.Add(c); idx.Add(i); lab.Add(l ?? ""); val.Add(v ?? ""); }
            void AddLog(string c, ILogEntryAccess log)
            {
                if (log == null) return;
                for (int i = 0; i < log.Length; i++) Add(c, i, log.Labels[i], log.Values[i]);
            }
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

            // Best-effort collection: any read failure logs and we still emit whatever was gathered.
            try
            {
                AddProps("instrument", Try(() => (object)raw.GetInstrumentData()));
                AddProps("sample",     Try(() => (object)raw.SampleInformation));
                AddProps("run_header", Try(() => (object)raw.RunHeaderEx));
                int tunes = Try(() => raw.GetTuneDataCount());
                for (int t = 0; t < tunes; t++) AddLog($"tune[{t}]", Try(() => raw.GetTuneData(t)));
                AddLog("status_log_header", Try(() => raw.GetStatusLogForRetentionTime(raw.RunHeaderEx.StartTime)));
                for (int m = 0; m < raw.InstrumentMethodsCount; m++) Add("instrument_method", m, $"method[{m}]", raw.GetInstrumentMethod(m));
            }
            catch (Exception ex) { Log.Warn($"vendor_file_metadata: partial read ({ex.Message})"); }

            var schema = VendorArrow.Schema(
                ("category",    new StringType(), false),
                ("entry_index", new Int32Type(),  false),
                ("label",       new StringType(), false),
                ("value",       new StringType(), false),
                ("value_float", new DoubleType(), true));

            var batch = VendorArrow.Batch(schema,
                VendorArrow.String(cat), VendorArrow.Int32(idx),
                VendorArrow.String(lab), VendorArrow.String(val),
                VendorArrow.NullableDouble(val.Select(ParseVendorFloat).ToList()));

            WriteEntry(writer, "vendor_file_metadata.parquet", schema, batch);
        }

        internal static void WriteVendorTrailerSchema(ThermoMZPeakWriter writer, List<VendorWideTrailerFacet.Column> cols)
        {
            var ord  = new List<int>(); var lab = new List<string>(); var dtype = new List<string>();
            var name = new List<string>(); var kind = new List<string>();
            for (int i = 0; i < cols.Count; i++)
            {
                ord.Add(i); lab.Add(cols[i].Label); dtype.Add(cols[i].DataType);
                name.Add(cols[i].Name); kind.Add(cols[i].Numeric ? "numeric" : "string");
            }

            var schema = VendorArrow.Schema(
                ("ordinal",      new Int32Type(),  false),
                ("label",        new StringType(), false),
                ("data_type",    new StringType(), false),
                ("column_name",  new StringType(), false),
                ("value_kind",   new StringType(), false));

            var batch = VendorArrow.Batch(schema,
                VendorArrow.Int32(ord), VendorArrow.String(lab), VendorArrow.String(dtype),
                VendorArrow.String(name), VendorArrow.String(kind));

            WriteEntry(writer, "vendor_trailer_schema.parquet", schema, batch);
        }

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
            try { for (int m = 0; m < raw.InstrumentMethodsCount; m++) methods.Add(raw.GetInstrumentMethod(m)); }
            catch (Exception ex) { Log.Warn($"vendor JSON: instrument-method read failed, sidecar incomplete ({ex.Message})"); }

            var schema = new JArray();
            try
            {
                var h = raw.GetTrailerExtraHeaderInformation();
                for (int i = 0; i < h.Length; i++)
                    schema.Add(new JObject { ["ordinal"] = i, ["label"] = h[i].Label, ["data_type"] = h[i].DataType.ToString() });
            }
            catch (Exception ex) { Log.Warn($"vendor JSON: trailer-schema read failed, sidecar incomplete ({ex.Message})"); }

            var root = new JObject
            {
                ["source_file"]        = sourceName,
                ["instrument"]         = Props(Try(() => (object)raw.GetInstrumentData())),
                ["sample"]             = Props(Try(() => (object)raw.SampleInformation)),
                ["run_header"]         = Props(Try(() => (object)raw.RunHeaderEx)),
                ["tune"]               = tune,
                ["status_log_header"]  = Entries(Try(() => raw.GetStatusLogForRetentionTime(raw.RunHeaderEx.StartTime))),
                ["instrument_methods"] = methods,
                ["trailer_schema"]     = schema
            };
            return root.ToString(Formatting.Indented);
        }

        private static void WriteEntry(ThermoMZPeakWriter writer, string name, Schema schema, RecordBatch batch)
        {
            var entry = new FileIndexEntry(name,
                new EntityType(EntityTypeTag.Other, "proprietary"), DataKind.Proprietary);
            using var managedStream = writer.StartProprietaryParquetEntry(entry);
            using var pw = VendorArrow.OpenWriter(managedStream, schema);
            pw.WriteRecordBatch(batch);
            pw.Close();
        }

        private static double? ParseVendorFloat(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            if (double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var d)) return d;
            if (double.TryParse(s, NumberStyles.Any, CultureInfo.CurrentCulture,   out d))     return d;
            return null;
        }

        private static T Try<T>(Func<T> f) { try { return f(); } catch { return default; } }
    }

    // Arrow array building helpers used by vendor facet code.
    internal static class VendorArrow
    {
        // Single source of truth for the proprietary-facet Parquet settings (zstd + embedded schema).
        public static FileWriter OpenWriter(ParquetSharp.IO.ManagedOutputStream sink, Schema schema)
        {
            var writerProps = new ParquetSharp.WriterPropertiesBuilder()
                .Compression(ParquetSharp.Compression.Zstd)
                .Build();
            var arrowProps = new ArrowWriterPropertiesBuilder().StoreSchema().Build();
            return new FileWriter(sink, schema, writerProps, arrowProps);
        }

        public static Schema Schema(params (string name, IArrowType type, bool nullable)[] fields)
        {
            var b = new Schema.Builder();
            foreach (var (n, t, nullable) in fields) b.Field(new Field(n, t, nullable));
            return b.Build();
        }

        public static RecordBatch Batch(Schema schema, params IArrowArray[] columns)
        {
            int length = columns.Length > 0 ? columns[0].Length : 0;
            return new RecordBatch(schema, columns, length);
        }

        public static IArrowArray Int32(IEnumerable<int> values)
        {
            var b = new Int32Array.Builder();
            foreach (var v in values) b.Append(v);
            return b.Build();
        }

        public static IArrowArray Double(IEnumerable<double> values)
        {
            var b = new DoubleArray.Builder();
            foreach (var v in values) b.Append(v);
            return b.Build();
        }

        public static IArrowArray NullableDouble(IEnumerable<double?> values)
        {
            var b = new DoubleArray.Builder();
            foreach (var v in values) { if (v.HasValue) b.Append(v.Value); else b.AppendNull(); }
            return b.Build();
        }

        public static IArrowArray String(IEnumerable<string> values)
        {
            var b = new StringArray.Builder();
            foreach (var v in values) b.Append(v ?? "");
            return b.Build();
        }
    }
}
