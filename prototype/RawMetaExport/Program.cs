using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Parquet;
using Parquet.Data;
using Parquet.Schema;
using ThermoFisher.CommonCore.Data.Business;
using ThermoFisher.CommonCore.Data.Interfaces;
using ThermoFisher.CommonCore.RawFileReader;

// Prototype: export the Thermo RAW metadata that mzML's CV vocabulary cannot represent — VERBATIM —
// into Parquet tables.
//
//   rawmetaexport <file.raw> <out-dir> [--max-scans N]
//
// Writes:
//   file_metadata.parquet   (category, entry_index, label, value, value_float)   — tune / sample /
//                            run_header / instrument / instrument_method / status_log_header
//   scan_trailers.parquet   (scan_index, label, value, value_float)              — the per-scan
//                            "Trailer Extra" bag, every label, exact string preserved
//
// Each value is kept as its exact source string (verbatim); value_float is a best-effort numeric
// parse (null when non-numeric) so the table is directly queryable. These would become
// `vendor_file_metadata` / `vendor_scan_trailers` facets inside an mzPeak archive.
class Program
{
    static async Task<int> Main(string[] args)
    {
        if (args.Length < 2) { Console.Error.WriteLine("usage: rawmetaexport <file.raw> <out-dir> [--max-scans N]"); return 2; }
        string path = args[0], outDir = args[1];
        int maxScans = int.MaxValue;
        string jsonPath = null;
        for (int i = 2; i < args.Length; i++)
        {
            if (args[i] == "--max-scans" && i + 1 < args.Length) maxScans = int.Parse(args[++i]);
            else if (args[i] == "--json" && i + 1 < args.Length) jsonPath = args[++i];
        }

        var raw = RawFileReaderFactory.ReadFile(path);
        if (raw == null || raw.IsError) { Console.Error.WriteLine("cannot open: " + path); return 2; }
        raw.SelectInstrument(Device.MS, 1);
        Directory.CreateDirectory(outDir);

        // JSON sidecar: file-level metadata only — instant even on a 22 GB file (no per-scan pass).
        if (jsonPath != null) { WriteVendorJson(raw, Path.GetFileName(path), jsonPath); Console.WriteLine("vendor json → " + jsonPath); }

        await WriteFileMetadata(raw, Path.Combine(outDir, "file_metadata.parquet"));
        if (maxScans > 0) await WriteScanTrailers(raw, Path.Combine(outDir, "scan_trailers.parquet"), maxScans);

        raw.Dispose();
        Console.WriteLine("done → " + outDir);
        return 0;
    }

    // ---- file-level metadata (one flat table, tagged by category) ---------------------------------
    static async Task WriteFileMetadata(IRawDataPlus raw, string outPath)
    {
        var cat = new List<string>(); var idx = new List<int>();
        var lab = new List<string>(); var val = new List<string>();

        void Add(string c, int i, string l, string v) { cat.Add(c); idx.Add(i); lab.Add(l ?? ""); val.Add(v ?? ""); }
        void AddLog(string c, ILogEntryAccess log) { if (log == null) return; for (int i = 0; i < log.Length; i++) Add(c, i, log.Labels[i], log.Values[i]); }
        void AddProps(string c, object o)
        {
            if (o == null) return; int i = 0;
            foreach (var p in o.GetType().GetProperties().Where(p => p.GetIndexParameters().Length == 0).OrderBy(p => p.Name))
            { object v; try { v = p.GetValue(o); } catch { continue; } if (v is System.Collections.IEnumerable && !(v is string)) continue; Add(c, i++, p.Name, v?.ToString()); }
        }

        AddProps("instrument", Try(() => (object)raw.GetInstrumentData()));
        AddProps("sample", Try(() => (object)raw.SampleInformation));
        AddProps("run_header", Try(() => (object)raw.RunHeaderEx));
        for (int t = 0; t < Try(() => raw.GetTuneDataCount()); t++) AddLog($"tune[{t}]", Try(() => raw.GetTuneData(t)));
        AddLog("status_log_header", Try(() => (ILogEntryAccess)raw.GetStatusLogForRetentionTime(raw.RunHeaderEx.StartTime)));
        try { for (int m = 0; m < raw.InstrumentMethodsCount; m++) Add("instrument_method", m, $"method[{m}]", raw.GetInstrumentMethod(m)); } catch { }

        var vf = val.Select(ParseFloat).ToArray();
        var schema = new ParquetSchema(
            new DataField<string>("category"), new DataField<int>("entry_index"),
            new DataField<string>("label"), new DataField<string>("value"), new DataField<double?>("value_float"));
        using var fs = File.Create(outPath);
        using var w = await ParquetWriter.CreateAsync(schema, fs);
        using var rg = w.CreateRowGroup();
        await rg.WriteColumnAsync(new DataColumn((DataField)schema[0], cat.ToArray()));
        await rg.WriteColumnAsync(new DataColumn((DataField)schema[1], idx.ToArray()));
        await rg.WriteColumnAsync(new DataColumn((DataField)schema[2], lab.ToArray()));
        await rg.WriteColumnAsync(new DataColumn((DataField)schema[3], val.ToArray()));
        await rg.WriteColumnAsync(new DataColumn((DataField)schema[4], vf));
        Console.WriteLine($"file_metadata.parquet: {cat.Count} rows");
    }

    // ---- per-scan trailer-extra bag (tall, streamed in row groups) --------------------------------
    static async Task WriteScanTrailers(IRawDataPlus raw, string outPath, int maxScans)
    {
        int first = raw.RunHeaderEx.FirstSpectrum, last = raw.RunHeaderEx.LastSpectrum;
        int end = (maxScans == int.MaxValue) ? last : Math.Min(last, first + maxScans - 1);

        var schema = new ParquetSchema(
            new DataField<ulong>("scan_index"), new DataField<string>("label"),
            new DataField<string>("value"), new DataField<double?>("value_float"));
        using var fs = File.Create(outPath);
        using var w = await ParquetWriter.CreateAsync(schema, fs);

        const int FlushRows = 1_000_000;
        var si = new List<ulong>(); var lab = new List<string>(); var val = new List<string>();
        long total = 0;

        async Task Flush()
        {
            if (si.Count == 0) return;
            using var rg = w.CreateRowGroup();
            await rg.WriteColumnAsync(new DataColumn((DataField)schema[0], si.ToArray()));
            await rg.WriteColumnAsync(new DataColumn((DataField)schema[1], lab.ToArray()));
            await rg.WriteColumnAsync(new DataColumn((DataField)schema[2], val.ToArray()));
            await rg.WriteColumnAsync(new DataColumn((DataField)schema[3], val.Select(ParseFloat).ToArray()));
            si.Clear(); lab.Clear(); val.Clear();
        }

        for (int s = first; s <= end; s++)
        {
            ILogEntryAccess t; try { t = raw.GetTrailerExtraInformation(s); } catch { continue; }
            for (int i = 0; i < t.Length; i++) { si.Add((ulong)s); lab.Add(t.Labels[i] ?? ""); val.Add(t.Values[i] ?? ""); }
            total += t.Length;
            if (si.Count >= FlushRows) await Flush();
        }
        await Flush();
        Console.WriteLine($"scan_trailers.parquet: {total} rows  (scans {first}..{end})");
    }

    // Same structure as the writer's --vendor-metadata-json sidecar (file-level only).
    static void WriteVendorJson(IRawDataPlus raw, string sourceName, string outPath)
    {
        JsonObject Props(object o)
        {
            var j = new JsonObject();
            if (o == null) return j;
            foreach (var p in o.GetType().GetProperties().Where(p => p.GetIndexParameters().Length == 0).OrderBy(p => p.Name))
            {
                object v; try { v = p.GetValue(o); } catch { continue; }
                if (v is System.Collections.IEnumerable && !(v is string)) continue;
                j[p.Name] = v?.ToString();
            }
            return j;
        }
        JsonArray Entries(ILogEntryAccess log)
        {
            var a = new JsonArray();
            if (log == null) return a;
            for (int i = 0; i < log.Length; i++) a.Add(new JsonObject { ["label"] = log.Labels[i], ["value"] = log.Values[i] });
            return a;
        }
        var tune = new JsonArray();
        for (int t = 0; t < Try(() => raw.GetTuneDataCount()); t++)
            tune.Add(new JsonObject { ["segment"] = t, ["entries"] = Entries(Try(() => raw.GetTuneData(t))) });
        var methods = new JsonArray();
        try { for (int m = 0; m < raw.InstrumentMethodsCount; m++) methods.Add(raw.GetInstrumentMethod(m)); } catch { }
        var schema = new JsonArray();
        try { var h = raw.GetTrailerExtraHeaderInformation(); for (int i = 0; i < h.Length; i++) schema.Add(new JsonObject { ["ordinal"] = i, ["label"] = h[i].Label, ["data_type"] = h[i].DataType.ToString() }); } catch { }
        var root = new JsonObject
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
        File.WriteAllText(outPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    static double? ParseFloat(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        if (double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var d)) return d;
        if (double.TryParse(s, NumberStyles.Any, CultureInfo.CurrentCulture, out d)) return d;
        return null;
    }

    static T Try<T>(Func<T> f) { try { return f(); } catch { return default; } }
}
